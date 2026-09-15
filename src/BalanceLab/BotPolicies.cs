using PetThem.Combat;

namespace PetThem.BalanceLab;

/// <summary>A deterministic input policy for a simulated run.</summary>
/// <remarks>
/// These bots are reproducible smoke tests. They do not model a human player's reaction time,
/// accuracy, or fatigue, and a result produced by one is not evidence about difficulty for people.
/// </remarks>
public interface IBotPolicy
{
    string Id { get; }
    string Description { get; }
    PlayerInput Decide(CombatWorld world, BalanceConfig config);
}

public static class BotPolicies
{
    public const string Default = "orbit-auto-punch-v1";

    private static readonly IBotPolicy[] Registry =
    {
        new OrbitAutoPunch(), new StillAutoPunch(), new FleeAutoPunch(),
    };

    public static IReadOnlyList<IBotPolicy> All => Registry;

    public static IBotPolicy Get(string? id)
    {
        string wanted = string.IsNullOrWhiteSpace(id) ? Default : id;
        return Registry.FirstOrDefault(policy => policy.Id == wanted)
            ?? throw new ArgumentException(
                $"Unknown policy '{wanted}'. Known policies: {string.Join(", ", Registry.Select(p => p.Id))}.");
    }

    /// <summary>Circles the arena centre and punches whenever the cooldown allows.</summary>
    /// <remarks>Behaviour is unchanged from the first simulator, so earlier runs stay comparable.</remarks>
    private sealed class OrbitAutoPunch : IBotPolicy
    {
        public string Id => "orbit-auto-punch-v1";
        public string Description =>
            "Walks a slow circle around the arena centre and auto-aims a punch every frame. Rarely gets touched.";

        public PlayerInput Decide(CombatWorld world, BalanceConfig config)
        {
            float angle = world.Time * 0.32f;
            var target = new Vec2((float)Math.Cos(angle) * config.arenaHalfWidth * .58f,
                (float)Math.Sin(angle) * config.arenaHalfHeight * .58f);
            return new PlayerInput((target - world.Position).Normalized, new Vec2(), true);
        }
    }

    /// <summary>Never moves. Shows what the same rules cost a player who only attacks.</summary>
    private sealed class StillAutoPunch : IBotPolicy
    {
        public string Id => "still-auto-punch-v1";
        public string Description =>
            "Stands on the starting spot and auto-aims a punch every frame. Takes contact damage, so it is the lower bound on movement skill.";

        public PlayerInput Decide(CombatWorld world, BalanceConfig config) =>
            new(new Vec2(), new Vec2(), true);
    }

    /// <summary>Backs away from the closest enemy, which pins it against the arena edge.</summary>
    private sealed class FleeAutoPunch : IBotPolicy
    {
        public string Id => "flee-auto-punch-v1";
        public string Description =>
            "Moves directly away from the nearest enemy and auto-aims a punch every frame. Ends up cornered, so it exposes edge crowding.";

        public PlayerInput Decide(CombatWorld world, BalanceConfig config)
        {
            var away = new Vec2();
            float closest = float.MaxValue;
            foreach (Enemy enemy in world.Enemies)
            {
                Vec2 delta = world.Position - enemy.Position;
                float distance = delta.Length;
                if (distance < closest) { closest = distance; away = delta; }
            }
            Vec2 move = away.Normalized;
            if (move.Length < 0.5f) move = new Vec2(1, 0);
            return new PlayerInput(move, new Vec2(), true);
        }
    }
}
