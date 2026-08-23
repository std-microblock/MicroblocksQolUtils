using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public readonly record struct RemotePlayer(
    int Id,
    string Name,
    Vector2 Position,
    string Room,
    Color Color
);

/// <summary>
/// Reflection-only adapter for MiaoNet 0.5+. The utility remains loadable when
/// MiaoNet is absent, and a MiaoNet update cannot become a hard loader failure.
/// </summary>
public static class MiaoNetBridge {
    private static readonly List<RemotePlayer> CurrentPlayers = [];
    private static readonly Dictionary<int, string> LastRooms = [];
    private static EverestModule? module;
    private static Assembly? assembly;
    private static bool commandInstalled;
    private static int retryFrames;
    private static int? originalOffscreenOpacity;
    private static PixelFont? avatarFont;
    private static float avatarBaseSize;
    private static Type? emojiType;

    public static IReadOnlyList<RemotePlayer> Players => CurrentPlayers;
    public static RemotePlayer? LocalPlayer { get; private set; }
    public static bool LoggedIn { get; private set; }
    public static int PlayersInMap { get; private set; } = 1;
    public static bool Available => module is not null;
    internal static bool CommandInstalled => commandInstalled;

    internal static bool SmokeValidate() {
        if (!TryResolve()) return true;
        if (!commandInstalled) TryInstallChatCommand();
        return commandInstalled;
    }

    public static void Update(Level? level) {
        if (level is null) return;
        if (!TryResolve()) {
            CurrentPlayers.Clear();
            LocalPlayer = null;
            LoggedIn = false;
            PlayersInMap = 1;
            return;
        }

        if (!commandInstalled && ++retryFrames >= 60) {
            retryFrames = 0;
            TryInstallChatCommand();
        }
        ApplyOffscreenNameSetting();
        ReadPlayers(level);
    }

    public static void Unload() {
        RestoreOffscreenNameSetting();
        CurrentPlayers.Clear();
        LocalPlayer = null;
        LoggedIn = false;
        LastRooms.Clear();
        module = null;
        assembly = null;
        avatarFont = null;
        emojiType = null;
        commandInstalled = false;
    }

    public static bool TryDrawAvatar(int playerId, Vector2 center, float size, Color color) {
        try {
            if (!TryResolveAvatarFont() || emojiType is null || avatarFont is null) return false;
            string key = $"\0mn_avt_{playerId}";
            object? registered = emojiType.GetProperty("Registered", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? emojiType.GetField("Registered", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (registered is IEnumerable values && !values.Cast<object>().Any(value => string.Equals(value?.ToString(), key, StringComparison.Ordinal)))
                return false;
            MethodInfo? get = emojiType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            object? startValue = emojiType.GetProperty("Start", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? emojiType.GetField("Start", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (get?.Invoke(null, [key]) is not int index || startValue is null) return false;
            int start = Convert.ToInt32(startValue);
            char character = (char)(start + index);
            float scale = size / Math.Max(1f, avatarBaseSize);
            avatarFont.Draw(avatarBaseSize, character, center, new Vector2(0.5f), new Vector2(scale), color);
            return true;
        } catch {
            return false;
        }
    }

    private static bool TryResolve() {
        if (module is not null) return true;
        module = Everest.Modules.FirstOrDefault(candidate => candidate.Metadata.Name == "MiaoNet");
        if (module is null) return false;
        assembly = module.GetType().Assembly;
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", $"MiaoNet integration enabled for {module.Metadata.VersionString}");
        return true;
    }

    private static void ReadPlayers(Level level) {
        CurrentPlayers.Clear();
        LocalPlayer = null;
        LoggedIn = false;
        PlayersInMap = 1;
        try {
            object context = module!.GetType().GetProperty("MiaoNetContext")!.GetValue(module)!;
            object? clientState = context.GetType().GetProperty("ClientState")?.GetValue(context);
            if (clientState is null) return;
            object? self = clientState.GetType().GetProperty("Self")?.GetValue(clientState);
            LoggedIn = self is not null;
            int selfId = self is null ? -1 : ReadInt(self, "ID", -1);
            if (self is not null) {
                object selfInfo = self.GetType().GetProperty("Info")!.GetValue(self)!;
                string selfName = (string)selfInfo.GetType().GetProperty("Name")!.GetValue(selfInfo)!;
                Color selfColor = (Color)selfInfo.GetType().GetProperty("Color")!.GetValue(selfInfo)!;
                string selfRoom = TryReadLocation(self, out _, out _, out string readRoom) ? readRoom : "";
                object? selfState = self.GetType().GetProperty("State")?.GetValue(self);
                Vector2 selfPosition = selfState is null
                    ? Vector2.Zero
                    : (Vector2)selfState.GetType().GetProperty("Position")!.GetValue(selfState)!;
                LocalPlayer = new RemotePlayer(selfId, selfName, selfPosition, selfRoom, selfColor);
            }
            List<object> allPlayers = EnumeratePlayers(clientState).Cast<object>().ToList();
            if (self is not null && allPlayers.All(player => ReadInt(player, "ID", -2) != selfId))
                allPlayers.Add(self);
            int count = 0;
            foreach (object player in allPlayers) {
                if (!TryReadLocation(player, out string sid, out int mode, out string room)) continue;
                if (!string.Equals(sid, level.Session.Area.SID, StringComparison.Ordinal)
                    || mode != (int)level.Session.Area.Mode) continue;
                count++;

                int id = ReadInt(player, "ID", -1);
                if (id == selfId) continue;
                object info = player.GetType().GetProperty("Info")!.GetValue(player)!;
                string name = (string)info.GetType().GetProperty("Name")!.GetValue(info)!;
                Color color = (Color)info.GetType().GetProperty("Color")!.GetValue(info)!;
                object? state = player.GetType().GetProperty("State")!.GetValue(player);
                if (state is not null) {
                    Vector2 position = (Vector2)state.GetType().GetProperty("Position")!.GetValue(state)!;
                    CurrentPlayers.Add(new RemotePlayer(id, name, position, room, color));
                }
                ObserveRoomChange(id, name, room);
            }
            PlayersInMap = Math.Max(1, count);
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MiaoNet");
            CurrentPlayers.Clear();
            LocalPlayer = null;
            LoggedIn = false;
            PlayersInMap = 1;
        }
    }

    private static IEnumerable EnumeratePlayers(object clientState) {
        object? allPlayers = clientState.GetType().GetProperty("AllPlayers")?.GetValue(clientState);
        if (allPlayers is IEnumerable direct) return direct;

        object? players = clientState.GetType().GetProperty("Players")?.GetValue(clientState);
        object? values = players?.GetType().GetProperty("Values")?.GetValue(players);
        return values as IEnumerable ?? Array.Empty<object>();
    }

    private static bool TryReadLocation(object player, out string sid, out int mode, out string room) {
        sid = "";
        mode = 0;
        room = "";
        object? location = player.GetType().GetProperty("Location")?.GetValue(player);
        if (location is null) return false;

        Type locationType = location.GetType();
        object? map = locationType.GetProperty("Map")?.GetValue(location);
        if (map is not null) {
            Type mapType = map.GetType();
            sid = mapType.GetProperty("Sid")?.GetValue(map)?.ToString() ?? "";
            mode = Convert.ToInt32(mapType.GetProperty("AreaMode")?.GetValue(map) ?? 0);
            room = locationType.GetProperty("Room")?.GetValue(location)?.ToString() ?? "";
        } else {
            sid = locationType.GetProperty("MapSid")?.GetValue(location)?.ToString() ?? "";
            mode = Convert.ToInt32(locationType.GetProperty("Side")?.GetValue(location) ?? 0);
            room = locationType.GetProperty("MapRoom")?.GetValue(location)?.ToString() ?? "";
        }
        return sid.Length > 0;
    }

    private static int ReadInt(object value, string property, int fallback) {
        object? result = value.GetType().GetProperty(property)?.GetValue(value);
        return result is null ? fallback : Convert.ToInt32(result);
    }

    private static void ObserveRoomChange(int id, string name, string room) {
        if (LastRooms.TryGetValue(id, out string? previous)
            && previous.Length > 0
            && room.Length > 0
            && !string.Equals(previous, room, StringComparison.Ordinal)
            && MicroblocksQolUtilsModule.Settings.WatchedPlayerNotifications
            && WatchList.Contains(name)
            && !WindowsNotifier.IsGameForeground()) {
            WindowsNotifier.Show("关注的玩家过面了", $"{name}: {previous} → {room}");
        }
        LastRooms[id] = room;
    }

    private static void ApplyOffscreenNameSetting() {
        try {
            Type? moduleType = assembly?.GetType("Celeste.Mod.MiaoNet.MiaoNetModule");
            object? settings = moduleType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            PropertyInfo? property = settings?.GetType().GetProperty("OffScreenPlayerNameOpacity");
            if (settings is null || property is null) return;
            int current = (int)property.GetValue(settings)!;
            if (MicroblocksQolUtilsModule.Settings.HideMiaoNetOffscreenNames) {
                originalOffscreenOpacity ??= current;
                if (current != 0) property.SetValue(settings, 0);
            } else if (originalOffscreenOpacity is int restore) {
                property.SetValue(settings, restore);
                originalOffscreenOpacity = null;
            }
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/MiaoNet", $"Cannot update offscreen-name opacity: {exception.Message}");
        }
    }

    private static void RestoreOffscreenNameSetting() {
        if (originalOffscreenOpacity is not int restore || assembly is null) return;
        try {
            Type? moduleType = assembly.GetType("Celeste.Mod.MiaoNet.MiaoNetModule");
            object? settings = moduleType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            settings?.GetType().GetProperty("OffScreenPlayerNameOpacity")?.SetValue(settings, restore);
        } catch { }
        originalOffscreenOpacity = null;
    }

    private static bool TryResolveAvatarFont() {
        if (avatarFont is not null && emojiType is not null) return true;
        Type? fontType = assembly?.GetType("Celeste.Mod.MiaoNet.MiaoNetFont");
        avatarFont = fontType?.GetProperty("ENZhsFont", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as PixelFont;
        avatarBaseSize = Convert.ToSingle(fontType?.GetProperty("ENZhsBaseSize", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? 64f);
        emojiType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(candidate => candidate.GetType("Celeste.Mod.Emoji", throwOnError: false))
            .FirstOrDefault(type => type is not null);
        return avatarFont is not null && emojiType is not null;
    }

    private static void TryInstallChatCommand() {
        try {
            Type commandType = assembly!.GetType("Celeste.Mod.MiaoNet.MiaoNetCommand", throwOnError: true)!;
            Type segmentType = assembly.GetType("Celeste.Mod.MiaoNet.CommandSegmentType", throwOnError: true)!;
            Type handlerType = commandType.GetNestedType("ExecuteHandler", BindingFlags.Public)!;
            Type contextType = commandType.GetNestedType("Context", BindingFlags.Public)!;
            ParameterExpression context = Expression.Parameter(contextType, "context");
            MethodInfo callback = typeof(MiaoNetBridge).GetMethod(nameof(ExecuteChatCommand), BindingFlags.NonPublic | BindingFlags.Static)!;
            Delegate handler = Expression.Lambda(
                handlerType,
                Expression.Call(callback, Expression.Convert(context, typeof(object))),
                context
            ).Compile();

            Array segments = Array.CreateInstance(segmentType, 1);
            segments.SetValue(Enum.Parse(segmentType, "Text"), 0);
            object command = Activator.CreateInstance(commandType, "qol", new[] { "mu" }, segments, true, handler)!;

            object contextObject = module!.GetType().GetProperty("MiaoNetContext")!.GetValue(module)!;
            object chat = contextObject.GetType().GetProperty("ChatComponent")!.GetValue(contextObject)!;
            object parser = chat.GetType().GetField("cmdParser", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(chat)!;
            FieldInfo commandsField = parser.GetType().GetField("commandsToMatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
            IEnumerable existing = (IEnumerable)commandsField.GetValue(parser)!;
            Type listType = typeof(List<>).MakeGenericType(commandType);
            IList replacement = (IList)Activator.CreateInstance(listType)!;
            foreach (object item in existing) replacement.Add(item);
            if (!replacement.Cast<object>().Any(item => string.Equals(commandType.GetProperty("Name")!.GetValue(item)?.ToString(), "qol", StringComparison.Ordinal)))
                replacement.Add(command);
            commandsField.SetValue(parser, replacement);
            commandInstalled = true;
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "Registered /qol (alias /mu) in the MiaoNet chat box");
        } catch (Exception exception) {
            Logger.Log(LogLevel.Verbose, "MicroblocksQolUtils/MiaoNet", $"MiaoNet chat command not ready: {exception.Message}");
        }
    }

    private static string? ExecuteChatCommand(object context) {
        try {
            IEnumerable segments = (IEnumerable)context.GetType().GetProperty("Segments")!.GetValue(context)!;
            string text = string.Join(" ", segments.Cast<object>().Select(value => value?.ToString() ?? "")).Trim();
            int separator = text.IndexOf(' ');
            string verb = (separator < 0 ? text : text[..separator]).Trim().ToLowerInvariant();
            string name = separator < 0 ? "" : text[(separator + 1)..].Trim();
            string message;
            switch (verb) {
                case "watch" when name.Length > 0:
                case "add" when name.Length > 0:
                case "关注" when name.Length > 0:
                    message = WatchList.Add(name) ? $"已关注 {name}" : $"已经关注 {name}";
                    break;
                case "unwatch" when name.Length > 0:
                case "remove" when name.Length > 0:
                case "取消关注" when name.Length > 0:
                    message = WatchList.Remove(name) ? $"已取消关注 {name}" : $"未关注 {name}";
                    break;
                case "list":
                case "列表":
                    message = WatchList.Describe();
                    break;
                default:
                    return "用法: /qol watch <玩家> | unwatch <玩家> | list";
            }
            context.GetType().GetMethod("TipMessage")?.Invoke(context, [message]);
            return null;
        } catch (Exception exception) {
            return exception.Message;
        }
    }
}
