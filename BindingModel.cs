using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.LoadoutSwitcher;

/// <summary>
/// JSON-serializable mirror of <see cref="DeepSlumberSetup"/>. The framework record uses
/// <see cref="System.Collections.Generic.IReadOnlyList{T}"/> and jagged <c>int[]</c> pairs, which
/// round-trip through <c>System.Text.Json</c> fine for reads but have no public settable
/// constructor-less shape for a config-section round trip; this plugin-local DTO gives
/// <c>System.Text.Json</c> plain settable properties to deserialize into, then maps to/from the
/// framework type at the boundary.
/// </summary>
internal sealed class BindingModel
{
    public int ProfessionId { get; set; }
    public List<AreaModel> Areas { get; set; } = new();

    /// <summary>One bound area: which area to enable and its middle-node phantom-factor sockets.</summary>
    public sealed class AreaModel
    {
        public int AreaId { get; set; }
        public List<int[]> Factors { get; set; } = new(); // [nodeId, itemId]
    }

    public static BindingModel From(DeepSlumberSetup s) => new()
    {
        ProfessionId = s.ProfessionId,
        Areas = s.Areas.Select(a => new AreaModel
        {
            AreaId = a.AreaId,
            Factors = a.Factors.Select(f => new[] { f[0], f[1] }).ToList(),
        }).ToList(),
    };

    public DeepSlumberSetup ToSetup() => new(
        ProfessionId,
        Areas.Select(a => new DeepSlumberAreaBinding(
            a.AreaId,
            a.Factors.Select(f => new[] { f[0], f[1] }).ToList())).ToList());
}
