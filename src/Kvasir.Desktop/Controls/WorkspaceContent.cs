using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Kvasir.Desktop.Controls;

// Navigation is provided by the sidebar. Expose the selected page's controls as
// a pane: TabControl's standard peer expects visible tab headers.
public sealed class WorkspaceContent : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new WorkspacePeer(this);
    private sealed class WorkspacePeer(WorkspaceContent owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => "Workspace";
    }
}
