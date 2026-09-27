using Colossal.Logging;
using Game;
using Game.Input;
using Game.Tools;
using Unity.Entities;

namespace TownRoadLane.Systems.Tool
{
    /// <summary>
    /// Polls the tool hotkey (Ctrl+M by default, rebindable in the mod settings) and the settings
    /// button, and toggles <see cref="MarkingNodeToolSystem"/> as the active tool.
    /// </summary>
    public partial class MarkingToolHotkeySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private ToolSystem _toolSystem;
        private DefaultToolSystem _defaultTool;
        private MarkingNodeToolSystem _markingTool;
        // Null when the settings or the binding failed to resolve; the hotkey is then inert.
        private ProxyAction _toggleAction;
        private bool _pendingButtonToggle;

        /// <summary>The one place, besides the hotkey, that toggles the tool: the settings
        /// "Activate marking tool" button calls it. The toggle is deferred to the next update: a
        /// settings setter runs outside the frame phase where activeTool can be switched.</summary>
        public static void RequestToggle()
        {
            var hotkeySystem = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MarkingToolHotkeySystem>();
            if (hotkeySystem == null) { log.Warn("MarkingToolHotkeySystem not found — cannot toggle"); return; }
            hotkeySystem._pendingButtonToggle = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _markingTool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();

            if (Mod.Settings == null)
            {
                log.Warn("MarkingToolHotkeySystem: settings not initialised, hotkey will not work");
                return;
            }
            // The ProxyAction reference is stable for the session and follows rebinds made in the
            // settings UI, so resolving it once is enough.
            _toggleAction = Mod.Settings.GetAction(TownRoadLaneSetting.ToggleMarkingTool);
            if (_toggleAction == null)
            {
                log.Warn($"MarkingToolHotkeySystem: OnCreate — GetAction('{TownRoadLaneSetting.ToggleMarkingTool}') returned null");
                return;
            }
            _toggleAction.shouldBeEnabled = true;
            log.Info($"MarkingToolHotkeySystem: OnCreate — action '{TownRoadLaneSetting.ToggleMarkingTool}' resolved, enabled");
        }

        protected override void OnDestroy()
        {
            if (_toggleAction != null) _toggleAction.shouldBeEnabled = false;
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            bool fromHotkey = _toggleAction != null && _toggleAction.WasPerformedThisFrame();
            bool fromButton = _pendingButtonToggle;
            _pendingButtonToggle = false;
            if (fromHotkey || fromButton)
                ToggleTool(fromHotkey ? "hotkey" : "button");
        }

        private void ToggleTool(string source)
        {
            if (_toolSystem.activeTool == _markingTool)
            {
                log.Debug($"{source}: deactivating MarkingNodeToolSystem");
                _toolSystem.activeTool = _defaultTool;
            }
            else
            {
                log.Debug($"{source}: activating MarkingNodeToolSystem");
                _toolSystem.activeTool = _markingTool;
            }
        }
    }
}
