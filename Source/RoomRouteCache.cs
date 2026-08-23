using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class RoomRouteCache {
    private static readonly ConditionalWeakTable<MapData, RoomGraph> Graphs = new();
    private static readonly IReadOnlySet<string> EmptyRoute = new HashSet<string>(StringComparer.Ordinal);

    public static int? RoomsToGoal(Level level) {
        MapData? map = level.Session.MapData;
        if (map is null || string.IsNullOrEmpty(level.Session.Level)) return null;
        return Graphs.GetValue(map, static value => new RoomGraph(value)).DistanceFrom(level.Session.Level);
    }

    public static IReadOnlySet<string> RouteToGoal(Level level) {
        MapData? map = level.Session.MapData;
        if (map is null || string.IsNullOrEmpty(level.Session.Level)) return EmptyRoute;
        return Graphs.GetValue(map, static value => new RoomGraph(value)).RouteFrom(level.Session.Level);
    }

    public static IReadOnlySet<string> NearbyRooms(Level level) {
        MapData? map = level.Session.MapData;
        if (map is null || string.IsNullOrEmpty(level.Session.Level)) return EmptyRoute;
        return Graphs.GetValue(map, static value => new RoomGraph(value)).NearbyFrom(level.Session.Level);
    }

    private sealed class RoomGraph {
        private readonly Dictionary<string, int> distance = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> fallbackDistance = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> edges;
        private readonly Dictionary<string, int> levelOrder;
        private readonly Dictionary<string, IReadOnlySet<string>> routes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlySet<string>> nearbyRooms = new(StringComparer.Ordinal);
        private readonly List<LevelData> levels;
        private readonly int fallbackGoalIndex;

        public RoomGraph(MapData map) {
            levels = map.Levels.Where(level => !level.Dummy).ToList();
            levelOrder = levels
                .Select((level, index) => (level.Name, index))
                .ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
            edges = levels.ToDictionary(level => level.Name, _ => new List<string>(), StringComparer.Ordinal);
            fallbackGoalIndex = levels.Count == 0 ? -1 : levels.Count - 1;
            if (levels.Count == 0) return;

            List<LevelData> goals = levels.Where(level => level.HasHeartGem).ToList();
            if (goals.Count == 0) goals.Add(levels[^1]);

            for (int index = 0; index < levels.Count; index++)
                fallbackDistance[levels[index].Name] = Math.Max(0, fallbackGoalIndex - index);

            for (int i = 0; i < levels.Count; i++) {
                for (int j = i + 1; j < levels.Count; j++) {
                    if (!TouchAlongEdge(levels[i].Bounds, levels[j].Bounds)) continue;
                    edges[levels[i].Name].Add(levels[j].Name);
                    edges[levels[j].Name].Add(levels[i].Name);
                }
            }

            Queue<string> queue = new();
            foreach (LevelData goal in goals) {
                distance[goal.Name] = 0;
                queue.Enqueue(goal.Name);
            }
            while (queue.Count > 0) {
                string current = queue.Dequeue();
                int nextDistance = distance[current] + 1;
                foreach (string next in edges[current]) {
                    if (distance.ContainsKey(next)) continue;
                    distance[next] = nextDistance;
                    queue.Enqueue(next);
                }
            }
        }

        public int? DistanceFrom(string room) {
            if (distance.TryGetValue(room, out int value)) return value;
            return fallbackDistance.TryGetValue(room, out int fallback) ? fallback : null;
        }

        public IReadOnlySet<string> RouteFrom(string room) {
            if (routes.TryGetValue(room, out IReadOnlySet<string>? cached)) return cached;

            HashSet<string> route = new(StringComparer.Ordinal);
            if (distance.TryGetValue(room, out int remaining)) {
                string current = room;
                route.Add(current);
                while (remaining > 0) {
                    string? next = edges[current]
                        .Where(candidate => distance.TryGetValue(candidate, out int value) && value == remaining - 1)
                        .OrderBy(candidate => levelOrder[candidate])
                        .FirstOrDefault();
                    if (next is null) break;
                    route.Add(next);
                    current = next;
                    remaining--;
                }
            } else if (levelOrder.TryGetValue(room, out int currentIndex) && fallbackGoalIndex >= currentIndex) {
                for (int index = currentIndex; index <= fallbackGoalIndex; index++)
                    route.Add(levels[index].Name);
            }

            routes[room] = route;
            return route;
        }

        public IReadOnlySet<string> NearbyFrom(string room) {
            if (nearbyRooms.TryGetValue(room, out IReadOnlySet<string>? cached)) return cached;
            if (!edges.TryGetValue(room, out List<string>? adjacent)) return EmptyRoute;

            HashSet<string> nearby = new(adjacent, StringComparer.Ordinal) { room };
            nearbyRooms[room] = nearby;
            return nearby;
        }

        private static bool TouchAlongEdge(Rectangle first, Rectangle second) {
            const int tolerance = 16;
            bool horizontal = (Math.Abs(first.Right - second.Left) <= tolerance
                    || Math.Abs(second.Right - first.Left) <= tolerance)
                && Math.Min(first.Bottom, second.Bottom) > Math.Max(first.Top, second.Top);
            bool vertical = (Math.Abs(first.Bottom - second.Top) <= tolerance
                    || Math.Abs(second.Bottom - first.Top) <= tolerance)
                && Math.Min(first.Right, second.Right) > Math.Max(first.Left, second.Left);
            return horizontal || vertical;
        }
    }

}
