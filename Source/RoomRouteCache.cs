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
        private readonly Dictionary<string, HashSet<string>> edges;
        private readonly Dictionary<string, HashSet<string>> predecessors;
        private readonly Dictionary<string, int> levelOrder;
        private readonly Dictionary<string, IReadOnlySet<string>> routes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlySet<string>> nearbyRooms = new(StringComparer.Ordinal);
        private readonly List<LevelData> levels;

        public RoomGraph(MapData map) {
            levels = map.Levels.Where(level => !level.Dummy).ToList();
            levelOrder = levels
                .Select((level, index) => (level.Name, index))
                .ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
            edges = levels.ToDictionary(
                level => level.Name,
                _ => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal
            );
            predecessors = levels.ToDictionary(
                level => level.Name,
                _ => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal
            );
            if (levels.Count == 0) return;

            foreach (LevelData level in levels) AddBoundaryTransitions(map, level);

            List<LevelData> goals = levels.Where(level => IsExplicitGoal(map, level)).ToList();
            if (goals.Count == 0) goals = FindFallbackGoals(map);

            Queue<string> queue = new();
            foreach (LevelData goal in goals) {
                if (!distance.TryAdd(goal.Name, 0)) continue;
                queue.Enqueue(goal.Name);
            }
            while (queue.Count > 0) {
                string current = queue.Dequeue();
                int nextDistance = distance[current] + 1;
                foreach (string previous in predecessors[current]) {
                    if (!distance.TryAdd(previous, nextDistance)) continue;
                    queue.Enqueue(previous);
                }
            }
        }

        public int? DistanceFrom(string room) => distance.TryGetValue(room, out int value) ? value : null;

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
            }

            routes[room] = route;
            return route;
        }

        public IReadOnlySet<string> NearbyFrom(string room) {
            if (nearbyRooms.TryGetValue(room, out IReadOnlySet<string>? cached)) return cached;
            if (!edges.TryGetValue(room, out HashSet<string>? outgoing)
                || !predecessors.TryGetValue(room, out HashSet<string>? incoming)) return EmptyRoute;

            HashSet<string> nearby = new(outgoing, StringComparer.Ordinal) { room };
            nearby.UnionWith(incoming);
            nearbyRooms[room] = nearby;
            return nearby;
        }

        private void AddBoundaryTransitions(MapData map, LevelData level) {
            BoundaryTiles boundary = new(level);
            AddSide(BoundarySide.Up, boundary.Width);
            AddSide(BoundarySide.Right, boundary.Height);
            if (!level.DisableDownTransition) AddSide(BoundarySide.Down, boundary.Width);
            AddSide(BoundarySide.Left, boundary.Height);

            void AddSide(BoundarySide side, int length) {
                for (int index = 0; index < length; index++) {
                    if (!boundary.IsOpen(side, index)) continue;
                    LevelData? target = map.GetAt(ProbeOutside(level.Bounds, side, index));
                    if (target is null || target.Dummy || target == level || !edges.ContainsKey(target.Name)) continue;
                    if (edges[level.Name].Add(target.Name)) predecessors[target.Name].Add(level.Name);
                }
            }
        }

        private List<LevelData> FindFallbackGoals(MapData map) {
            List<LevelData> named = levels.Where(level => LooksLikeEndRoom(level.Name)).ToList();
            if (named.Count > 0) return named;

            LevelData? start = map.StartLevel();
            if (start is null || !edges.ContainsKey(start.Name)) return [levels[^1]];

            Dictionary<string, int> fromStart = new(StringComparer.Ordinal) { [start.Name] = 0 };
            Queue<string> queue = new();
            queue.Enqueue(start.Name);
            while (queue.Count > 0) {
                string current = queue.Dequeue();
                foreach (string next in edges[current]) {
                    if (!fromStart.TryAdd(next, fromStart[current] + 1)) continue;
                    queue.Enqueue(next);
                }
            }

            List<LevelData> endpoints = levels.Where(level => {
                if (!fromStart.ContainsKey(level.Name) || level.Name == start.Name) return false;
                return edges[level.Name].Count == 0
                    || edges[level.Name].Concat(predecessors[level.Name]).Distinct(StringComparer.Ordinal).Count() <= 1;
            }).ToList();
            if (endpoints.Count == 0) endpoints = levels.Where(level => fromStart.ContainsKey(level.Name)).ToList();
            if (endpoints.Count == 0) return [levels[^1]];

            int farthest = endpoints.Max(level => fromStart[level.Name]);
            return endpoints.Where(level => fromStart[level.Name] == farthest).ToList();
        }

        private static bool IsExplicitGoal(MapData map, LevelData level) {
            if (HeartCompletesArea(map) && level.HasHeartGem) return true;
            return level.Entities.Any(IsCompletionMarker) || level.Triggers.Any(IsCompletionMarker);
        }

        private static bool HeartCompletesArea(MapData map) {
            bool? configured = map.Meta?.HeartIsEnd;
            if (configured.HasValue) return configured.Value;
            return map.Data?.LevelSet == "Celeste"
                && (map.Area.Mode != AreaMode.Normal || map.Area.ID == 9);
        }

        private static bool IsCompletionMarker(EntityData entity) {
            string name = EntityBaseName(entity.Name);
            if (name is "goldenberrycollecttrigger" or "completeareatrigger" or "miniheart") return true;
            if (entity.Bool("endLevel")) return true;
            if (name != "eventtrigger") return false;

            string eventName = entity.Attr("event").Trim().ToLowerInvariant();
            return eventName.StartsWith("end_", StringComparison.Ordinal)
                || eventName.EndsWith("_end", StringComparison.Ordinal)
                || eventName.Contains("_ending", StringComparison.Ordinal);
        }

        private static string EntityBaseName(string name) {
            int separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            return name[(separator + 1)..].ToLowerInvariant();
        }

        private static bool LooksLikeEndRoom(string name) {
            string normalized = name.Trim().ToLowerInvariant();
            return normalized is "end" or "ending" or "finish" or "final"
                || normalized.StartsWith("end_", StringComparison.Ordinal)
                || normalized.StartsWith("end-", StringComparison.Ordinal);
        }

        private static Vector2 ProbeOutside(Rectangle bounds, BoundarySide side, int index) => side switch {
            BoundarySide.Up => new Vector2(bounds.Left + index * 8 + 4, bounds.Top - 5),
            BoundarySide.Right => new Vector2(bounds.Right + 4, bounds.Top + index * 8 + 4),
            BoundarySide.Down => new Vector2(bounds.Left + index * 8 + 4, bounds.Bottom + 4),
            _ => new Vector2(bounds.Left - 5, bounds.Top + index * 8 + 4)
        };
    }

    private sealed class BoundaryTiles {
        private readonly string[] rows;

        public int Width { get; }
        public int Height { get; }

        public BoundaryTiles(LevelData level) {
            Width = level.TileBounds.Width;
            Height = level.TileBounds.Height;
            rows = level.Solids.Split(["\r\n", "\n\r", "\n", "\r"], StringSplitOptions.None);
        }

        public bool IsOpen(BoundarySide side, int index) => side switch {
            BoundarySide.Up => IsOpenTile(index, 0),
            BoundarySide.Right => IsOpenTile(Width - 1, index),
            BoundarySide.Down => IsOpenTile(index, Height - 1),
            _ => IsOpenTile(0, index)
        };

        private bool IsOpenTile(int x, int y) {
            if (y < 0 || y >= rows.Length) return true;
            string row = rows[y];
            return x < 0 || x >= row.Length || row[x] == '0';
        }
    }

    private enum BoundarySide {
        Up,
        Right,
        Down,
        Left
    }
}
