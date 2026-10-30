using Content.Server.Chat.Managers;
using Content.Shared._CorvaxNext.CrewMedal;
using Content.Shared._Crescent.Overwatch;
using Content.Shared._Crescent.Payment;
using Content.Shared._Crescent.Territory;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Cargo.Events;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.Chemistry;
using Content.Shared.Communications;
using Content.Shared.CriminalRecords;
using Content.Shared.DeltaV.CartridgeLoader.Cartridges;
using Content.Shared.Fax;
using Content.Shared.IdentityManagement;
using Content.Shared.Labels;
using Content.Shared.MachineLinking;
using Content.Shared.MassMedia.Components;
using Content.Shared.NukeOps;
using Content.Shared.Paper;
using Content.Shared.Pinpointer;
using Content.Shared.PsionicsRecords;
using Content.Shared.Renamable.EntitySystems;
using Content.Shared.Shipyard;
using Content.Shared.Silicons.Borgs;
using Content.Shared.Soul;
using Content.Shared.Speech.Components;
using Content.Shared.SurveillanceCamera;
using Content.Shared.VoiceMask;
using Robust.Shared.Player;

namespace Content.Server._Crescent.Chat;

public sealed class SootProtocolWritingSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chat = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BoundUserInterfaceMessageAttempt>(OnMessageAttempt);
    }

    private void OnMessageAttempt(BoundUserInterfaceMessageAttempt args)
    {
        if (args.Cancelled || !TryComp<ActorComponent>(args.Actor, out var actor))
            return;

        if (GetWrittenText(args) is not { } text || string.IsNullOrWhiteSpace(text))
            return;

        if (_chat.SootProtocolBlocks(actor.PlayerSession, text, "soot-protocol-blocked-writing"))
            args.Cancel();
    }

    // anything typed here ends up where other players can read it
    private string? GetWrittenText(BoundUserInterfaceMessageAttempt args)
    {
        return args.Message switch
        {
            SharedPaperComponent.PaperInputTextMessage paper => paper.Text,
            HandLabelerLabelChangedMessage labeler => labeler.Label,
            CommunicationsConsoleAnnounceMessage announcement => announcement.Message,
            CommunicationsConsoleBroadcastMessage broadcast => broadcast.Message,
            NewsWriterPublishMessage article => $"{article.Title} {article.Content}",
            CartridgeUiMessage { MessageEvent: NotekeeperUiMessageEvent { Action: NotekeeperUiAction.Add } note } => note.Note,
            CartridgeUiMessage { MessageEvent: NanoChatUiMessageEvent nanoChat } => nanoChat.Type switch
            {
                NanoChatUiMessageType.SendMessage
                    or NanoChatUiMessageType.NewChat
                    or NanoChatUiMessageType.EditChat => nanoChat.Content,
                _ => null,
            },
            MeleeSpeechBattlecryChangedMessage battlecry => battlecry.Battlecry,
            FaxFileMessage fax => $"{fax.Label} {fax.Content}",
            CargoConsoleAddOrderMessage order => Identity.Name(args.Actor, EntityManager) == order.Requester
                ? order.Reason
                : $"{order.Requester} {order.Reason}",
            CriminalRecordChangeStatus status => status.Reason,
            CriminalRecordAddHistory history => history.Line,
            PsionicsRecordChangeStatus psionics => psionics.Reason,
            IdCardConsoleComponent.WriteToTargetIdMessage id => ChangedIdText(args.Target, id),
            AgentIDCardNameChangedMessage agentName => agentName.Name,
            AgentIDCardJobChangedMessage agentJob => agentJob.Job,
            VoiceMaskChangeNameMessage voiceMask => voiceMask.Name,
            BorgSetNameBuiMessage borg => borg.Name,
            GolemNameChangedMessage golem => golem.Name,
            RenamableBuiMessage rename => rename.Name,
            ShipyardConsolePurchaseMessage ship => ship.CustomName,
            TerritoryConsoleRenameMessage territory => territory.Name,
            NavMapBeaconConfigureBuiMessage beacon => beacon.Text,
            SignalTimerTextChangedMessage timer => timer.Text,
            SurveillanceCameraSetupSetName camera => camera.Name,
            ChemMasterCreatePillsMessage pills => pills.Label,
            ChemMasterOutputToBottleMessage bottle => bottle.Label,
            CrewMedalReasonChangedMessage medal => medal.Reason,
            WarDeclaratorActivateMessage war => war.Message,
            OverwatchSendMessageAnnouncement overwatch => overwatch.Message,
            PaymentBonusMessage bonus => bonus.Reason,
            _ => null,
        };
    }

    // a muted hop can still fix access, just not write a new name or job on the card
    private string? ChangedIdText(EntityUid console, IdCardConsoleComponent.WriteToTargetIdMessage message)
    {
        if (!TryComp<IdCardConsoleComponent>(console, out var consoleComp)
            || !TryComp<IdCardComponent>(consoleComp.TargetIdSlot.Item, out var card))
        {
            return $"{message.FullName} {message.JobTitle}";
        }

        var name = message.FullName == (card.FullName ?? string.Empty) ? null : message.FullName;
        var job = message.JobTitle == card.LocalizedJobTitle ? null : message.JobTitle;
        return $"{name} {job}";
    }
}
