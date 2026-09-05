using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Stellar.LoadoutSwitcher.Tests;

/// <summary>
/// Pins the Deep-Slumber binding storage move from the shared plugin config to the plugin's own data
/// store (<c>bindings.json</c>) — owner ruling 2026-09-05 ("each plugin can create their own
/// plugindata … user old config should be migrated there"). The migration tests are named for that
/// ruling; they guard the rollback contract (legacy config keys left frozen, plugindata wins per
/// loadout, a consulted id is never consulted again so a cleared binding cannot be resurrected).
/// </summary>
public sealed class BindingPersistenceTests
{
    private static BindingModel Binding(int professionId, params int[] areaIds)
    {
        var model = new BindingModel { ProfessionId = professionId };
        foreach (var areaId in areaIds)
        {
            model.Areas.Add(new BindingModel.AreaModel
            {
                AreaId = areaId,
                Factors = new List<int[]> { new[] { 100, 20020935 }, new[] { 101, 20020907 } },
                NormalNodes = new List<int> { 1001, 1002 },
            });
        }
        return model;
    }

    // ---- serialize / deserialize -------------------------------------------------------------

    [Fact]
    public void Round_trip_preserves_profession_areas_factors_and_anchor_nodes()
    {
        var doc = new BindingsDocument();
        doc.Bindings[1] = Binding(5, 1, 20002);
        doc.MarkMigrated(1);

        var back = BindingPersistence.Deserialize(BindingPersistence.Serialize(doc), out var corrupt);

        Assert.False(corrupt);
        var model = back.Bindings[1];
        Assert.Equal(5, model.ProfessionId);
        Assert.Equal(new[] { 1, 20002 }, model.Areas.ConvertAll(a => a.AreaId));
        Assert.Equal(new[] { 100, 20020935 }, model.Areas[0].Factors[0]);
        Assert.Equal(new[] { 101, 20020907 }, model.Areas[0].Factors[1]);
        Assert.Equal(new[] { 1001, 1002 }, model.Areas[0].NormalNodes);
        Assert.Equal(new[] { 1 }, back.MigratedConfigIds);
    }

    [Fact]
    public void Round_trip_maps_through_the_framework_setup_type_unchanged()
    {
        var doc = new BindingsDocument();
        doc.Bindings[3] = Binding(9, 7);

        var back = BindingPersistence.Deserialize(BindingPersistence.Serialize(doc), out _);
        var setup = back.Bindings[3].ToSetup();

        Assert.Equal(9, setup.ProfessionId);
        var area = Assert.Single(setup.Areas);
        Assert.Equal(7, area.AreaId);
        Assert.Equal(2, area.Factors.Count);
        Assert.Equal(new[] { 1001, 1002 }, area.NormalNodes!);
    }

    [Fact]
    public void A_legacy_binding_with_no_captured_tree_stays_null_not_empty()
    {
        // NormalNodes null = "captured before tree support"; the reconciler treats it as factor-only
        // and leaves the live tree alone. An empty LIST means "the exact target is no anchors" and
        // DOES reset. Collapsing the two would silently change what an old binding applies.
        var doc = new BindingsDocument();
        doc.Bindings[2] = new BindingModel
        {
            ProfessionId = 2,
            Areas = new List<BindingModel.AreaModel>
            {
                new() { AreaId = 1, Factors = new List<int[]> { new[] { 100, 20020907 } }, NormalNodes = null },
                new() { AreaId = 2, Factors = new List<int[]>(), NormalNodes = new List<int>() },
            },
        };

        var back = BindingPersistence.Deserialize(BindingPersistence.Serialize(doc), out _);

        Assert.Null(back.Bindings[2].Areas[0].NormalNodes);
        Assert.NotNull(back.Bindings[2].Areas[1].NormalNodes);
        Assert.Empty(back.Bindings[2].Areas[1].NormalNodes!);
    }

    [Fact]
    public void Serialized_output_is_compact_utf8_with_no_newline_or_indent()
    {
        var doc = new BindingsDocument();
        doc.Bindings[1] = Binding(5, 1);

        var text = Encoding.UTF8.GetString(BindingPersistence.Serialize(doc));

        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain(": ", text);
        Assert.StartsWith("{\"Bindings\":{\"1\":{", text);
    }

    // ---- tolerant decode ---------------------------------------------------------------------

    [Fact]
    public void Absent_file_decodes_to_an_empty_document_and_is_not_corrupt()
    {
        var doc = BindingPersistence.Deserialize(null, out var corrupt);

        Assert.False(corrupt);
        Assert.Empty(doc.Bindings);
        Assert.Empty(doc.MigratedConfigIds);
    }

    [Fact]
    public void Empty_file_decodes_to_an_empty_document_and_is_not_corrupt()
    {
        var doc = BindingPersistence.Deserialize(System.Array.Empty<byte>(), out var corrupt);

        Assert.False(corrupt);
        Assert.Empty(doc.Bindings);
    }

    [Fact]
    public void Garbage_decodes_to_an_empty_document_and_flags_corrupt()
    {
        var doc = BindingPersistence.Deserialize(Encoding.UTF8.GetBytes("{not json"), out var corrupt);

        Assert.True(corrupt);
        Assert.Empty(doc.Bindings);
    }

    [Fact]
    public void Truncated_json_flags_corrupt_instead_of_throwing()
    {
        var doc = new BindingsDocument();
        doc.Bindings[1] = Binding(5, 1, 20002);
        var bytes = BindingPersistence.Serialize(doc);
        var truncated = new byte[bytes.Length / 2];
        System.Array.Copy(bytes, truncated, truncated.Length);

        var back = BindingPersistence.Deserialize(truncated, out var corrupt);

        Assert.True(corrupt);
        Assert.Empty(back.Bindings);
    }

    [Fact]
    public void Literal_null_document_flags_corrupt()
    {
        var doc = BindingPersistence.Deserialize(Encoding.UTF8.GetBytes("null"), out var corrupt);

        Assert.True(corrupt);
        Assert.Empty(doc.Bindings);
    }

    [Fact]
    public void Null_members_are_normalized_away_so_a_decoded_binding_is_safe_to_map()
    {
        var json = "{\"Bindings\":{\"1\":null,\"2\":{\"ProfessionId\":2,\"Areas\":null}},\"MigratedConfigIds\":null}";

        var doc = BindingPersistence.Deserialize(Encoding.UTF8.GetBytes(json), out var corrupt);

        Assert.False(corrupt);
        Assert.False(doc.Bindings.ContainsKey(1));
        Assert.Empty(doc.Bindings[2].Areas);
        Assert.Empty(doc.MigratedConfigIds);
        Assert.Equal(2, doc.Bindings[2].ToSetup().ProfessionId);
    }

    [Fact]
    public void A_malformed_factor_pair_is_dropped_rather_than_crashing_the_mapping()
    {
        var json = "{\"Bindings\":{\"1\":{\"ProfessionId\":5,\"Areas\":[{\"AreaId\":1,\"Factors\":[[100],null,[101,20020907]]}]}}}";

        var doc = BindingPersistence.Deserialize(Encoding.UTF8.GetBytes(json), out var corrupt);

        Assert.False(corrupt);
        var factors = doc.Bindings[1].Areas[0].Factors;
        Assert.Equal(new[] { 101, 20020907 }, Assert.Single(factors));
    }

    // ---- the migration ledger ----------------------------------------------------------------

    [Fact]
    public void MarkMigrated_records_an_id_once_and_reports_whether_it_changed()
    {
        var doc = new BindingsDocument();

        Assert.True(doc.MarkMigrated(1));
        Assert.False(doc.MarkMigrated(1));
        Assert.True(doc.MarkMigrated(10));
        Assert.Equal(new[] { 1, 10 }, doc.MigratedConfigIds);
    }

    [Fact]
    public void A_reloaded_ledger_still_refuses_ids_it_already_consulted_owner_2026_09_05_plugindata()
    {
        // The whole point of persisting the ledger: session 2 must not re-read the frozen config keys
        // for ids session 1 already handled, or a binding cleared in session 1 comes back.
        var doc = new BindingsDocument();
        doc.MarkMigrated(1);
        doc.MarkMigrated(2);

        var reloaded = BindingPersistence.Deserialize(BindingPersistence.Serialize(doc), out _);

        Assert.False(reloaded.MarkMigrated(1));
        Assert.False(reloaded.MarkMigrated(2));
        Assert.True(reloaded.MarkMigrated(3));
    }

    // ---- migration decision (owner ruling 2026-09-05, plugindata) ----------------------------

    [Fact]
    public void Decide_uses_plugindata_even_when_the_frozen_config_still_binds_that_loadout_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.UsePluginData, BindingPersistence.Decide(pluginDataHasBinding: true, configHasBinding: true));
    }

    [Fact]
    public void Decide_uses_plugindata_when_it_is_the_only_source_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.UsePluginData, BindingPersistence.Decide(pluginDataHasBinding: true, configHasBinding: false));
    }

    [Fact]
    public void Decide_migrates_when_only_the_config_binds_that_loadout_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.MigrateFromConfig, BindingPersistence.Decide(pluginDataHasBinding: false, configHasBinding: true));
    }

    [Fact]
    public void Decide_starts_empty_when_neither_source_binds_that_loadout_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.StartEmpty, BindingPersistence.Decide(pluginDataHasBinding: false, configHasBinding: false));
    }

    [Fact]
    public void Migrating_the_owners_real_config_shape_preserves_every_binding_owner_2026_09_05_plugindata()
    {
        // Shape taken from the owner's live stellar.loadoutswitcher.config.json (MAIN client,
        // 2026-09-05): section "loadout-deepslumber", keys binding.1# / binding.2# / binding.3#,
        // classes 5 / 2 / 9, areas 1 + 20002 (binding.3# has empty factor lists).
        var doc = new BindingsDocument();
        foreach (var (id, prof) in new[] { (1, 5), (2, 2), (3, 9) })
        {
            var fromConfig = Binding(prof, 1, 20002);
            if (BindingPersistence.Decide(doc.Bindings.ContainsKey(id), configHasBinding: true)
                == MigrationDecision.MigrateFromConfig)
            {
                doc.Bindings[id] = fromConfig;
            }
            doc.MarkMigrated(id);
        }

        var reloaded = BindingPersistence.Deserialize(BindingPersistence.Serialize(doc), out var corrupt);

        Assert.False(corrupt);
        Assert.Equal(3, reloaded.Bindings.Count);
        Assert.Equal(5, reloaded.Bindings[1].ProfessionId);
        Assert.Equal(2, reloaded.Bindings[2].ProfessionId);
        Assert.Equal(9, reloaded.Bindings[3].ProfessionId);
        Assert.Equal(new[] { 1, 20002 }, reloaded.Bindings[3].Areas.ConvertAll(a => a.AreaId));
        Assert.Equal(new[] { 1, 2, 3 }, reloaded.MigratedConfigIds);
    }
}
