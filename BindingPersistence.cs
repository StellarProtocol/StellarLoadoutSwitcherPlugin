using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stellar.LoadoutSwitcher;

/// <summary>Where the binding for ONE loadout comes from — the outcome of
/// <see cref="BindingPersistence.Decide"/>.</summary>
internal enum MigrationDecision
{
    /// <summary>Plugindata already holds a binding for this loadout; use it and ignore the frozen
    /// legacy config key entirely.</summary>
    UsePluginData,

    /// <summary>Plugindata holds nothing for this loadout but the legacy config key does: copy it in
    /// once. The config key is LEFT IN PLACE.</summary>
    MigrateFromConfig,

    /// <summary>Neither source holds a binding for this loadout — it is simply unbound.</summary>
    StartEmpty,
}

/// <summary>
/// The <c>bindings.json</c> document: the live per-loadout Deep-Slumber bindings, plus the ledger of
/// loadout ids whose legacy config key has already been consulted.
/// </summary>
internal sealed class BindingsDocument
{
    /// <summary>Loadout id → its bound Deep-Slumber setup. Legacy GLOBAL mirror — kept in sync with the
    /// CURRENT character's set (see <see cref="Characters"/>) purely for rollback safety, so a build
    /// predating per-character bindings still finds the right (current-character) bindings.</summary>
    public Dictionary<int, BindingModel> Bindings { get; set; } = new();

    /// <summary>Loadout ids whose legacy <c>binding.&lt;id&gt;#</c> config key has already been read
    /// (whether or not it held anything). The legacy keys are never deleted, so without this ledger a
    /// later migration pass would resurrect a binding the user had cleared.</summary>
    public List<int> MigratedConfigIds { get; set; } = new();

    /// <summary>Authoritative per-character store: charId → (loadoutId → binding). Keyed on the
    /// framework's <c>PlayerState.CharId</c> (owner ruling — bindings must not leak across characters
    /// or accounts sharing one client).</summary>
    public Dictionary<long, Dictionary<int, BindingModel>> Characters { get; set; } = new();

    /// <summary>Set once the pre-feature global <see cref="Bindings"/> have been re-homed onto a
    /// character. Never re-runs after that, even for a different character, so a second character never
    /// inherits the first character's pre-migration set.</summary>
    public bool MigratedGlobalToChar { get; set; }

    // Not serialized (private fields are invisible to System.Text.Json) — an O(1) mirror of
    // MigratedConfigIds, built on first use.
    private HashSet<int>? _migrated;

    /// <summary>Record <paramref name="loadoutId"/> as consulted. Returns true when it was NOT already
    /// recorded — i.e. the caller changed the document and should persist it.</summary>
    public bool MarkMigrated(int loadoutId)
    {
        _migrated ??= new HashSet<int>(MigratedConfigIds);
        if (!_migrated.Add(loadoutId)) return false;
        MigratedConfigIds.Add(loadoutId);
        return true;
    }
}

/// <summary>
/// Serialization + the per-loadout source decision for the Deep-Slumber bindings.
///
/// <para><b>Where bindings live (owner ruling 2026-09-05).</b> A binding is user DATA — the player
/// captured it with "Bind current" — so it lives in the plugin's OWN per-plugin data store,
/// <c>&lt;game_mini&gt;/stellar/plugindata/stellar.loadoutswitcher.data/bindings.json</c> (the
/// framework's <c>IPluginDataStore</c>), not in the shared
/// <c>stellar/plugins/stellar.loadoutswitcher.config.json</c>. That file stays for SETTINGS — here
/// just the <c>autoApply</c> toggle. One document (rather than one file per loadout) because the
/// binding set and its migration ledger must move together: the store's write is a single atomic
/// temp + rename, so a crash can never leave half a set on disk.</para>
///
/// <para><b>Rollback safety (process rules § 6).</b> The migration COPIES; it never deletes. Every
/// <c>binding.&lt;id&gt;#</c> config key is left exactly as it was and is never written again, so
/// rolling back to 2.3.0 still finds the bindings the user had when the migration ran. Coming back to
/// this build ignores that frozen copy: plugindata wins per loadout, and consulted ids are recorded so
/// a cleared binding is never resurrected.</para>
///
/// <para>Only the plugin's own DTOs are touched here, so the unit-test project compiles this file
/// directly.</para>
/// </summary>
internal static class BindingPersistence
{
    /// <summary>Plugindata file name holding the whole binding document.</summary>
    internal const string FileName = "bindings.json";

    /// <summary>Where unreadable bytes are parked before the live file is rewritten — user bytes we
    /// could not parse are never destroyed, only moved aside.</summary>
    internal const string CorruptFileName = "bindings.corrupt.json";

    // Compact (no indent, no newlines) — a machine file. Relaxed escaping keeps it readable if the
    // owner ever opens it.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize the document to compact UTF-8 JSON.</summary>
    /// <param name="doc">The document to store.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    internal static byte[] Serialize(BindingsDocument doc) => JsonSerializer.SerializeToUtf8Bytes(doc, Options);

    /// <summary>Decode a stored document, tolerantly: absent/empty input yields an empty document and
    /// <paramref name="corrupt"/> false; unparseable input yields an empty document and
    /// <paramref name="corrupt"/> true so the caller can log it and park the bytes. Never throws.</summary>
    /// <param name="data">The bytes read from the data store, or null when the file is absent.</param>
    /// <param name="corrupt">True when <paramref name="data"/> held something that could not be decoded.</param>
    /// <returns>The decoded document, normalized; never null.</returns>
    internal static BindingsDocument Deserialize(byte[]? data, out bool corrupt)
    {
        corrupt = false;
        if (data is null || data.Length == 0) return new BindingsDocument();
        try
        {
            var doc = JsonSerializer.Deserialize<BindingsDocument>(data, Options);
            if (doc is null)
            {
                corrupt = true;   // literal "null" — not a usable document
                return new BindingsDocument();
            }
            return Normalize(doc);
        }
        catch (JsonException)
        {
            corrupt = true;
            return new BindingsDocument();
        }
        catch (NotSupportedException)
        {
            corrupt = true;
            return new BindingsDocument();
        }
    }

    /// <summary>Which source the binding for ONE loadout comes from. Plugindata ALWAYS wins when it
    /// holds a binding for that loadout — even though the frozen legacy config key is still on disk —
    /// so a rollback-and-return round trip never overwrites newer work with the pre-migration copy.</summary>
    /// <param name="pluginDataHasBinding">True when the document already binds this loadout.</param>
    /// <param name="configHasBinding">True when the legacy <c>binding.&lt;id&gt;#</c> key decoded to a binding.</param>
    /// <returns>The source to use for this loadout.</returns>
    internal static MigrationDecision Decide(bool pluginDataHasBinding, bool configHasBinding)
        => pluginDataHasBinding ? MigrationDecision.UsePluginData
         : configHasBinding ? MigrationDecision.MigrateFromConfig
         : MigrationDecision.StartEmpty;

    /// <summary>One-time re-home of the pre-per-character global <see cref="BindingsDocument.Bindings"/>
    /// onto <paramref name="charId"/>. COPIES — the legacy <see cref="BindingsDocument.Bindings"/> mirror
    /// is left in place for rollback (a pre-feature build still reads the right, current-character, set).
    /// No-ops once <see cref="BindingsDocument.MigratedGlobalToChar"/> is set (so a second/different
    /// character never inherits the first character's pre-migration bindings) or while
    /// <paramref name="charId"/> is unresolved (<c>0</c>).</summary>
    /// <param name="doc">The document to migrate in place.</param>
    /// <param name="charId">The resolved current character id (<c>PlayerState.CharId</c>).</param>
    internal static void MigrateGlobalToCharacter(BindingsDocument doc, long charId)
    {
        if (doc.MigratedGlobalToChar || charId == 0) return;   // already re-homed, or id not resolved yet
        if (doc.Bindings.Count > 0)
        {
            var dst = doc.Characters.TryGetValue(charId, out var d) ? d : (doc.Characters[charId] = new());
            foreach (var kv in doc.Bindings) dst[kv.Key] = kv.Value;   // COPY — the legacy Bindings mirror is kept
        }
        doc.MigratedGlobalToChar = true;                       // set even when there was nothing to copy (fresh install)
    }

    /// <summary>Drop null members from a decoded document so every binding is safe to map to a
    /// <c>DeepSlumberSetup</c> without null checks. A null <c>NormalNodes</c> is PRESERVED — it is the
    /// legacy "tree was never captured" marker the reconciler keys on (factor-only, no reset).</summary>
    /// <param name="doc">A decoded document (may be null).</param>
    /// <returns>The same document instance, cleaned; a fresh empty one when null.</returns>
    internal static BindingsDocument Normalize(BindingsDocument? doc)
    {
        if (doc is null) return new BindingsDocument();
        doc.Bindings ??= new Dictionary<int, BindingModel>();
        doc.MigratedConfigIds ??= new List<int>();
        doc.Characters ??= new Dictionary<long, Dictionary<int, BindingModel>>();

        var clean = new Dictionary<int, BindingModel>(doc.Bindings.Count);
        foreach (var kv in doc.Bindings)
        {
            if (kv.Value is null) continue;
            kv.Value.Areas ??= new List<BindingModel.AreaModel>();
            kv.Value.Areas.RemoveAll(static a => a is null);
            foreach (var area in kv.Value.Areas)
            {
                area.Factors ??= new List<int[]>();
                area.Factors.RemoveAll(static f => f is null || f.Length < 2);
            }
            clean[kv.Key] = kv.Value;
        }
        doc.Bindings = clean;
        return doc;
    }
}
