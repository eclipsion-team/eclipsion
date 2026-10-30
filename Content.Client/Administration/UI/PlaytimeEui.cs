using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;

namespace Content.Client.Administration.UI;

public sealed class PlaytimeEui : BaseEui
{
    private PlaytimeWindow Window;

    public PlaytimeEui()
    {
        Window = new PlaytimeWindow();
        Window.OnClose += () => SendMessage(new CloseEuiMessage());
        Window.OnSelectPlayer += player => SendMessage(new SelectPlaytimePlayerMessage(player));
        Window.OnChange += (trackers, time, overwrite) => SendMessage(new ChangePlaytimeMessage(trackers, time, overwrite));
        Window.OnMeetRequirements += jobs => SendMessage(new MeetPlaytimeRequirementsMessage(jobs));
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not PlaytimeEuiState cast)
            return;

        Window.HandleState(cast);
    }

    public override void Opened()
    {
        base.Opened();

        Window.OpenCentered();
    }

    public override void Closed()
    {
        base.Closed();

        Window.Close();
        Window.Dispose();
    }
}
