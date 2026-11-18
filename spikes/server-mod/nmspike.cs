// NativeMods spike S4: an ordinary LocalExile script mod (drop this file into Mods/Binaries/nmspike/). It drives a
// native client window through plain chat lines, so it needs no Gateway change. "##NM <verb> <args>" lines are taken
// by the client host and never shown; the window's buttons answer with the /nm chat command below.
using System.Collections.Concurrent;

public sealed class NmSpike : IMod
{
    static readonly ConcurrentDictionary<string, List<string>> Gardens = new();

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;

        GameEvents.Command(id, "garden", "/garden (opens the NativeMods spike window)", async (conn, args) =>
        {
            await conn.Commands!.Reply("##NM open Metadata/UI/Mods/Spike/Garden.ui");
            await conn.Commands!.Reply($"##NM set body.status Welcome, {conn.Character}. This text was sent by a server mod.");
        });

        GameEvents.Command(id, "nm", "/nm plant <seed> | harvest (sent by the spike window's buttons)", async (conn, args) =>
        {
            var seeds = Gardens.GetOrAdd($"{conn.Character}", _ => new());
            string text;
            lock (seeds)
            {
                if (args.Length > 0 && args[0] == "plant")
                {
                    string seed = args.Length > 1 ? string.Join(' ', args[1..]) : "Unnamed Seed";
                    seeds.Add(seed);
                    text = $"The server planted \"{seed}\". {seeds.Count} seed(s) growing: {string.Join(", ", seeds)}.";
                }
                else if (args.Length > 0 && args[0] == "harvest")
                {
                    text = seeds.Count == 0 ? "Nothing to harvest yet." : $"The server harvested {seeds.Count} seed(s): {string.Join(", ", seeds)}.";
                    seeds.Clear();
                }
                else text = "Unknown request.";
            }
            await conn.Commands!.Reply("##NM set body.status " + text);
        });
    }
}
