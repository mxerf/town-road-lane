using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Input;
using Game.Tools;
using Unity.Entities;

namespace TownRoadLane
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
        private ProxyAction _toggleAction;
        private bool _pendingButtonToggle;

        /// <summary>Called from the settings "Activate marking tool" button. The toggle is deferred
        /// to the next update: a settings setter runs outside the frame phase where activeTool can
        /// be switched.</summary>
        public static void RequestToggle()
        {
            var sys = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MarkingToolHotkeySystem>();
            if (sys == null) { log.Warn("MarkingToolHotkeySystem not found — cannot toggle"); return; }
            sys._pendingButtonToggle = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _markingTool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            // The ProxyAction reference is stable for the session and follows rebinds made in the
            // settings UI, so resolving it once is enough.
            if (Mod.Settings != null)
            {
                _toggleAction = Mod.Settings.GetAction(TownRoadLaneSetting.ToggleMarkingTool);
                if (_toggleAction != null)
                {
                    _toggleAction.shouldBeEnabled = true;
                    log.Info($"MarkingToolHotkeySystem: OnCreate — action '{TownRoadLaneSetting.ToggleMarkingTool}' resolved, enabled");
                }
                else
                {
                    log.Warn($"MarkingToolHotkeySystem: OnCreate — GetAction('{TownRoadLaneSetting.ToggleMarkingTool}') returned null");
                }
            }
            else
            {
                log.Warn("MarkingToolHotkeySystem: settings not initialised, hotkey will not work");
            }
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
            if (fromButton) _pendingButtonToggle = false;
            if (!fromHotkey && !fromButton) return;

            string src = fromHotkey ? "hotkey" : "button";
            if (_toolSystem.activeTool == _markingTool)
            {
                log.Info($"{src}: deactivating MarkingNodeToolSystem");
                _toolSystem.activeTool = _defaultTool;
            }
            else
            {
                log.Info($"{src}: activating MarkingNodeToolSystem");
                _toolSystem.activeTool = _markingTool;
            }
        }
    }
}
