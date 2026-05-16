using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Input;
using Game.Tools;
using Unity.Entities;

namespace TownRoadLane
{
    /// <summary>
    /// Polls the Ctrl+M hotkey (configurable via the mod's keybinding settings) and toggles
    /// <see cref="MarkingNodeToolSystem"/> as the active tool. Idle every frame except for
    /// the cheap WasPerformedThisFrame check.
    ///
    /// Pattern mirrors Traffic's ModUISystem hotkey toggle (ModUISystem.cs:134-136):
    /// read ProxyAction from Setting.GetAction(name) and flip m_ToolSystem.activeTool.
    /// </summary>
    public partial class MarkingToolHotkeySystem : GameSystemBase
    {
        private static readonly ILog log = Mod.log;

        private ToolSystem _toolSystem;
        private DefaultToolSystem _defaultTool;
        private MarkingNodeToolSystem _markingTool;
        private ProxyAction _toggleAction;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _markingTool = World.GetOrCreateSystemManaged<MarkingNodeToolSystem>();
            // Resolve the action once at OnCreate — the ProxyAction reference is stable across the
            // session and reflects rebinds the user makes through the mod's settings UI.
            if (Mod.Settings != null)
            {
                _toggleAction = Mod.Settings.GetAction(Setting.ToggleMarkingTool);
                if (_toggleAction != null)
                {
                    _toggleAction.shouldBeEnabled = true;
                    log.Info($"MarkingToolHotkeySystem: OnCreate — action '{Setting.ToggleMarkingTool}' resolved, enabled");
                }
                else
                {
                    log.Warn($"MarkingToolHotkeySystem: OnCreate — GetAction('{Setting.ToggleMarkingTool}') returned null");
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
            if (_toggleAction == null) return;
            if (!_toggleAction.WasPerformedThisFrame()) return;

            if (_toolSystem.activeTool == _markingTool)
            {
                log.Info("hotkey: deactivating MarkingNodeToolSystem");
                _toolSystem.activeTool = _defaultTool;
            }
            else
            {
                log.Info("hotkey: activating MarkingNodeToolSystem");
                _toolSystem.activeTool = _markingTool;
            }
        }
    }
}
