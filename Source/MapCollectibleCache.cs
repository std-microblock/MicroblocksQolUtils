using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum MiniMapCollectibleKind {
    Strawberry,
    GoldenBerry,
    MoonBerry,
    Heart,
    Cassette,
    Key,
    Gem
}

internal readonly record struct MiniMapCollectible(
    string Room,
    int EntityId,
    Vector2 Position,
    MiniMapCollectibleKind Kind,
    int Index = -1
) {
    public bool IsCollected(Session session) {
        if (EntityId >= 0) {
            EntityID id = new(Room, EntityId);
            if (session.DoNotLoad.Contains(id)) return true;
            if (Kind is MiniMapCollectibleKind.Strawberry
                or MiniMapCollectibleKind.GoldenBerry
                or MiniMapCollectibleKind.MoonBerry) {
                if (session.Strawberries.Contains(id)) return true;
                return SaveData.Instance?.CheckStrawberry(session.Area, id) == true;
            }
            if (Kind == MiniMapCollectibleKind.Key) return session.Keys.Contains(id);
        }

        return Kind switch {
            MiniMapCollectibleKind.Heart => session.HeartGem,
            MiniMapCollectibleKind.Cassette => session.Cassette,
            MiniMapCollectibleKind.Gem when Index >= 0 && Index < session.SummitGems.Length => session.SummitGems[Index],
            _ => false
        };
    }
}

internal static class MapCollectibleCache {
    private static readonly ConditionalWeakTable<MapData, CollectibleMap> Maps = new();

    public static IReadOnlyList<MiniMapCollectible> Get(MapData map) =>
        Maps.GetValue(map, static value => new CollectibleMap(value)).Collectibles;

    private sealed class CollectibleMap {
        public List<MiniMapCollectible> Collectibles { get; } = [];

        public CollectibleMap(MapData map) {
            HashSet<EntityData> berries = new(map.Strawberries);
            HashSet<EntityData> goldenBerries = new(map.Goldenberries);
            goldenBerries.UnionWith(map.DashlessGoldenberries);

            foreach (LevelData room in map.Levels) {
                if (room.Dummy) continue;
                bool foundHeart = false;
                foreach (EntityData entity in room.Entities) {
                    MiniMapCollectibleKind? kind = Classify(entity, berries, goldenBerries);
                    if (kind is null) continue;
                    if (kind == MiniMapCollectibleKind.Heart) foundHeart = true;
                    Collectibles.Add(new MiniMapCollectible(
                        room.Name,
                        entity.ID,
                        room.Position + entity.Position,
                        kind.Value,
                        kind == MiniMapCollectibleKind.Gem ? entity.Int("gem", -1) : -1
                    ));
                }

                if (room.HasHeartGem && !foundHeart) {
                    Collectibles.Add(new MiniMapCollectible(
                        room.Name,
                        -1,
                        new Vector2(room.Bounds.Center.X, room.Bounds.Center.Y),
                        MiniMapCollectibleKind.Heart
                    ));
                }
            }
        }

        private static MiniMapCollectibleKind? Classify(
            EntityData entity,
            HashSet<EntityData> berries,
            HashSet<EntityData> goldenBerries
        ) {
            string name = entity.Name.Replace("_", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .ToLowerInvariant();

            if (goldenBerries.Contains(entity) || name.Contains("goldenberry", StringComparison.Ordinal))
                return MiniMapCollectibleKind.GoldenBerry;
            if (berries.Contains(entity) || name.Contains("strawberry", StringComparison.Ordinal)
                || name.EndsWith("berry", StringComparison.Ordinal))
                return name.Contains("moonberry", StringComparison.Ordinal)
                    ? MiniMapCollectibleKind.MoonBerry
                    : MiniMapCollectibleKind.Strawberry;
            if (name.Contains("heartgem", StringComparison.Ordinal) || name.EndsWith("blackgem", StringComparison.Ordinal))
                return MiniMapCollectibleKind.Heart;
            if (name == "cassette" || name.EndsWith("/cassette", StringComparison.Ordinal))
                return MiniMapCollectibleKind.Cassette;
            if (name == "key" || name.EndsWith("/key", StringComparison.Ordinal))
                return MiniMapCollectibleKind.Key;
            if (name.Contains("summitgem", StringComparison.Ordinal))
                return MiniMapCollectibleKind.Gem;
            return null;
        }
    }
}
