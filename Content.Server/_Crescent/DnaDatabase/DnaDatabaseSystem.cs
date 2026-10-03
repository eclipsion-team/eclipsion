using System.Linq;
using Content.Server.Announcements.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Crescent.DnaDatabase;
using Content.Shared.Customization.Systems;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.DnaDatabase;

public sealed class DnaDatabaseSystem : EntitySystem
{
    [Dependency] private readonly AnnouncerSystem _announcer = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly StationJobsSystem _stationJobs = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DnaDatabaseComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<DnaDatabaseComponent, DnaDatabaseToggleMessage>(OnToggleMessage);
        SubscribeLocalEvent<DnaDatabaseComponent, BoundUIOpenedEvent>(OnUiOpened);
    }

    private void OnInit(Entity<DnaDatabaseComponent> ent, ref ComponentInit args)
    {
        var station = _station.GetOwningStation(ent.Owner);
        if (station == null)
            return;

        BindToStation(ent, station.Value);
    }

    private void BindToStation(Entity<DnaDatabaseComponent> ent, EntityUid station)
    {
        ent.Comp.BoundStation = station;

        if (TryComp<TransformComponent>(ent.Owner, out var xform) && xform.GridUid is { } grid)
            ent.Comp.BoundGrid = grid;
        else if (TryComp<StationDataComponent>(station, out var data))
            ent.Comp.BoundGrid = _station.GetLargestGrid(data) ?? EntityUid.Invalid;

        Dirty(ent);
        UpdateUI(ent);
    }

    private void OnUiOpened(Entity<DnaDatabaseComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUI(ent);
    }

    private void OnToggleMessage(Entity<DnaDatabaseComponent> ent, ref DnaDatabaseToggleMessage args)
    {
        if (args.Actor is not { Valid: true })
            return;

        var now = _timing.CurTime;
        if (now < ent.Comp.NextToggle)
            return;

        ent.Comp.NextToggle = now + ent.Comp.ToggleCooldown;
        ApplyToggle(ent);
    }

    public void ApplyToggle(Entity<DnaDatabaseComponent> ent)
    {
        if (string.IsNullOrEmpty(ent.Comp.FactionId))
            return;

        if (ent.Comp.BoundStation == EntityUid.Invalid ||
            !TryComp<StationJobsComponent>(ent.Comp.BoundStation, out var stationJobs))
            return;

        ent.Comp.Enabled = !ent.Comp.Enabled;
        Dirty(ent);

        if (ent.Comp.Enabled)
            RestoreJobSlots((ent.Comp.BoundStation, stationJobs), ent.Comp);
        else
            SaveAndClearJobSlots((ent.Comp.BoundStation, stationJobs), ent.Comp);

        var factionName = GetFactionName(ent.Comp.FactionId);
        var key = ent.Comp.Enabled ? "dna-database-announce-enabled" : "dna-database-announce-disabled";
        var color = ent.Comp.Enabled ? new Color(0.3f, 0.9f, 0.4f) : new Color(1f, 0.35f, 0.35f);
        Announce(Loc.GetString(key, ("faction", factionName)), color);

        UpdateUI(ent);
    }

    private void SaveAndClearJobSlots(Entity<StationJobsComponent> station, DnaDatabaseComponent database)
    {
        database.SavedJobSlots.Clear();
        foreach (var (jobId, slots) in station.Comp.JobList.ToList())
        {
            if (!_prototypes.TryIndex<JobPrototype>(jobId, out var job))
                continue;

            if (GetJobFaction(job) != database.FactionId)
                continue;

            database.SavedJobSlots[jobId] = slots;
            _stationJobs.TrySetJobSlot(station, jobId, 0, false, station.Comp);
        }
    }

    private void RestoreJobSlots(Entity<StationJobsComponent> station, DnaDatabaseComponent database)
    {
        foreach (var (jobId, slots) in database.SavedJobSlots)
        {
            if (slots == null)
                _stationJobs.MakeJobUnlimited(station, jobId, station.Comp);
            else
                _stationJobs.TrySetJobSlot(station, jobId, (int)slots.Value, false, station.Comp);
        }

        database.SavedJobSlots.Clear();
    }

    public bool IsRecruitmentBlocked(EntityUid station, string? jobId)
    {
        if (jobId == null || !TryGetDisabledDatabase(station, out _))
            return false;

        if (!_prototypes.TryIndex<JobPrototype>(jobId, out var job))
            return false;

        var jobFaction = GetJobFaction(job);
        if (jobFaction == null)
            return false;

        if (!TryGetDisabledDatabase(station, out var database))
            return false;

        return database.FactionId == jobFaction;
    }

    private bool TryGetDisabledDatabase(EntityUid station, out DnaDatabaseComponent database)
    {
        var query = EntityQueryEnumerator<DnaDatabaseComponent>();
        while (query.MoveNext(out _, out var comp))
        {
            if (comp.BoundStation == station && !comp.Enabled)
            {
                database = comp;
                return true;
            }
        }

        database = default!;
        return false;
    }

    private void UpdateUI(Entity<DnaDatabaseComponent> ent)
    {
        var total = 0;
        var available = 0;
        var unlimited = 0;

        if (ent.Comp.BoundStation != EntityUid.Invalid &&
            TryComp<StationJobsComponent>(ent.Comp.BoundStation, out var jobs))
        {
            if (ent.Comp.Enabled)
                CountFactionJobs(ent.Comp.BoundStation, jobs, ent.Comp.FactionId, ref total, ref available, ref unlimited);
            else
                CountSavedJobs(ent.Comp, ref total, ref available, ref unlimited);
        }

        var factionName = GetFactionName(ent.Comp.FactionId);
        var state = new DnaDatabaseBoundUserInterfaceState(
            ent.Comp.Enabled,
            ent.Comp.FactionId,
            factionName,
            total,
            available,
            unlimited);

        _ui.SetUiState(ent.Owner, DnaDatabaseUiKey.Key, state);
    }

    private void CountFactionJobs(
        EntityUid station,
        StationJobsComponent jobs,
        string factionId,
        ref int total,
        ref int available,
        ref int unlimited)
    {
        foreach (var (jobId, slots) in jobs.JobList)
        {
            if (!_prototypes.TryIndex<JobPrototype>(jobId, out var job))
                continue;

            if (GetJobFaction(job) != factionId)
                continue;

            if (slots == null)
            {
                unlimited++;
                continue;
            }

            if (slots == 0)
                continue;

            total += (int)slots.Value;
            available += (int)slots.Value;
        }

        foreach (var jobId in _stationJobs.GetOverflowJobs(station, jobs))
        {
            if (!_prototypes.TryIndex<JobPrototype>(jobId, out var job))
                continue;

            if (GetJobFaction(job) == factionId)
                unlimited++;
        }
    }

    private void CountSavedJobs(DnaDatabaseComponent database, ref int total, ref int available, ref int unlimited)
    {
        available = 0;

        foreach (var slots in database.SavedJobSlots.Values)
        {
            if (slots == null)
                unlimited++;
            else
                total += (int)slots.Value;
        }

    }

    private static string? GetJobFaction(JobPrototype job)
    {
        foreach (var req in job.Requirements ?? [])
        {
            if (req is FactionRequirement factionReq)
                return factionReq.FactionID;
        }

        return null;
    }

    private string GetFactionName(string factionId)
    {
        if (string.IsNullOrEmpty(factionId))
            return Loc.GetString("dna-database-unknown-faction");

        return Loc.TryGetString($"faction-{factionId}", out var name)
            ? name
            : factionId;
    }

    private void Announce(string msg, Color color)
    {
        _announcer.SendAnnouncementMessage("fallback", msg,
            sender: Loc.GetString("dna-database-announcer-name"), colorOverride: color);
    }
}
