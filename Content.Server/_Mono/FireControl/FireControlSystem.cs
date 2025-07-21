// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 RikuTheKiller
// SPDX-FileCopyrightText: 2025 ScyronX
// SPDX-FileCopyrightText: 2025 ark1368
// SPDX-FileCopyrightText: 2025 sleepyyapril
// SPDX-FileCopyrightText: 2025 starch
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// Copyright Rane (elijahrane@gmail.com) 2025
// All rights reserved. Relicensed under AGPL with permission

using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Mono.FireControl;
using Content.Shared.Power;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using System.Linq;
using Content.Shared.Physics;
using System.Numerics;
using Content.Server.Power.EntitySystems;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Timing;
using Content.Shared.Interaction;
using Content.Shared._Mono.ShipGuns;
using Content.Shared.Examine;

namespace Content.Server._Mono.FireControl;

public sealed partial class FireControlSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<FireControlServerComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<FireControlServerComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<FireControlServerComponent, ExaminedEvent>(OnExamined);

        SubscribeLocalEvent<FireControllableComponent, PowerChangedEvent>(OnControllablePowerChanged);
        SubscribeLocalEvent<FireControllableComponent, ComponentShutdown>(OnControllableShutdown);

        InitializeConsole();
    }

    private void OnPowerChanged(EntityUid uid, FireControlServerComponent component, PowerChangedEvent args)
    {
        if (args.Powered)
            TryConnect(uid, component);
        else
            Disconnect(uid, component);
    }

    private void OnShutdown(EntityUid uid, FireControlServerComponent component, ComponentShutdown args)
    {
        Disconnect(uid, component);
    }

    private void OnExamined(EntityUid uid, FireControlServerComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;
        args.PushMarkup(
            Loc.GetString(
                "gunnery-server-examine-detail",
                ("usedProcessingPower", component.UsedProcessingPower),
                ("processingPower", component.ProcessingPower),
                ("valueColor", component.UsedProcessingPower <= component.ProcessingPower - 2 ? "green" : "yellow")
            )
        );
    }

    private void OnControllablePowerChanged(EntityUid uid, FireControllableComponent component, PowerChangedEvent args)
    {
        if (args.Powered)
            TryRegister(uid, component);
        else
            Unregister(uid, component);
    }

    private void OnControllableShutdown(EntityUid uid, FireControllableComponent component, ComponentShutdown args)
    {
        if (component.ControllingServer != null && TryComp<FireControlServerComponent>(component.ControllingServer, out var server))
        {
            Unregister(uid, component);

            foreach (var console in server.Consoles)
            {
                if (TryComp<FireControlConsoleComponent>(console, out var consoleComp))
                {
                    UpdateUi(console, consoleComp);
                }
            }
        }
    }

    private void Disconnect(EntityUid server, FireControlServerComponent? component = null)
    {
        if (!Resolve(server, ref component))
            return;

        if (!Exists(component.ConnectedGrid) || !TryComp<FireControlGridComponent>(component.ConnectedGrid, out var controlGrid))
            return;

        if (controlGrid.ControllingServer == server)
        {
            controlGrid.ControllingServer = null;
            RemComp<FireControlGridComponent>((EntityUid)component.ConnectedGrid);
        }

        foreach (var controllable in(component.Controlled))
            Unregister(controllable);

        foreach (var console in component.Consoles)
            UnregisterConsole(console);
    }

    public void RefreshControllables(EntityUid grid, FireControlGridComponent? component = null)
    {
        if (!Resolve(grid, ref component))
            return;

        if (component.ControllingServer == null || !TryComp<FireControlServerComponent>(component.ControllingServer, out var server))
            return;

        server.Controlled.Clear();
        server.UsedProcessingPower = 0;

        var query = EntityQueryEnumerator<FireControllableComponent>();

        while (query.MoveNext(out var controllable, out var controlComp))
        {
            if (_xform.GetGrid(controllable) == grid)
                TryRegister(controllable, controlComp);
        }

        foreach (var console in server.Consoles)
            UpdateUi(console);
    }

    private bool TryConnect(EntityUid server, FireControlServerComponent? component = null)
    {
        if (!Resolve(server, ref component))
            return false;

        var grid = _xform.GetGrid(server);

        if (grid == null)
            return false;

        var controlGrid = EnsureComp<FireControlGridComponent>((EntityUid)grid);

        if (controlGrid.ControllingServer != null)
            return false;

        controlGrid.ControllingServer = server;
        component.ConnectedGrid = grid;

        RefreshControllables((EntityUid)grid, controlGrid);

        return true;
    }

    private void Unregister(EntityUid controllable, FireControllableComponent? component = null)
    {
        if (!Resolve(controllable, ref component))
            return;

        if (component.ControllingServer == null || !TryComp<FireControlServerComponent>(component.ControllingServer, out var controlComp))
            return;

        controlComp.Controlled.Remove(controllable);
        controlComp.UsedProcessingPower -= GetProcessingPowerCost(controllable, component);
        component.ControllingServer = null;
    }

    private bool TryRegister(EntityUid controllable, FireControllableComponent? component = null)
    {
        if (!Resolve(controllable, ref component))
            return false;

        var gridServer = TryGetGridServer(controllable);

        if (gridServer.ServerUid == null || gridServer.ServerComponent == null)
            return false;

        var processingPowerCost = GetProcessingPowerCost(controllable, component);

        if (processingPowerCost > GetRemainingProcessingPower(gridServer.ServerUid.Value, gridServer.ServerComponent))
            return false;

        if (gridServer.ServerComponent.Controlled.Add(controllable))
        {
            gridServer.ServerComponent.UsedProcessingPower += processingPowerCost;
            component.ControllingServer = gridServer.ServerUid;
            return true;
        }
        else
        {
            return false;
        }
    }

    public int GetRemainingProcessingPower(EntityUid server, FireControlServerComponent? component = null)
    {
        if (!Resolve(server, ref component))
            return 0;

        return component.ProcessingPower - component.UsedProcessingPower;
    }

    public int GetProcessingPowerCost(EntityUid controllable, FireControllableComponent? component = null)
    {
        if (!Resolve(controllable, ref component))
            return 0;

        if (!TryComp<ShipGunClassComponent>(controllable, out var classComponent))
            return 0;

        return classComponent.Class switch
        {
            ShipGunClass.Superlight => 1,
            ShipGunClass.Light => 3,
            ShipGunClass.Medium => 6,
            ShipGunClass.Heavy => 9,
            ShipGunClass.Superheavy => 12,
            _ => 0,
        };
    }

    private (EntityUid? ServerUid, FireControlServerComponent? ServerComponent) TryGetGridServer(EntityUid uid)
    {
        var grid = _xform.GetGrid(uid);

        if (grid == null)
            return (null, null);

        if (!TryComp<FireControlGridComponent>(grid, out var controlGrid))
            return (null, null);

        if (controlGrid.ControllingServer == null || !TryComp<FireControlServerComponent>(controlGrid.ControllingServer, out var server))
            return (null, null);

        return (controlGrid.ControllingServer, server);
    }

    public void FireWeapons(EntityUid server, List<NetEntity> weapons, NetCoordinates coordinates, FireControlServerComponent? component = null)
    {
        if (!Resolve(server, ref component))
            return;

        var targetCoords = GetCoordinates(coordinates);

        foreach (var weapon in weapons)
        {
            var localWeapon = GetEntity(weapon);
            if (!component.Controlled.Contains(localWeapon))
                continue;

            if (!TryComp<GunComponent>(localWeapon, out var gun))
                continue;

            if (TryComp<TransformComponent>(localWeapon, out var weaponXform))
            {
                var currentMapCoords = _xform.GetMapCoordinates(localWeapon, weaponXform);
                var destinationMapCoords = targetCoords.ToMap(EntityManager, _xform);

                if (destinationMapCoords.MapId == currentMapCoords.MapId && currentMapCoords.MapId != MapId.Nullspace)
                {
                    var diff = destinationMapCoords.Position - currentMapCoords.Position;
                    if (TryComp<FireControlRotateComponent>(localWeapon, out var rotateEnabled))
                    if (diff.LengthSquared() > 0.01f)
                    {
                        // Only rotate the gun if it has line of sight to the target
                        if (HasLineOfSight(localWeapon, currentMapCoords.Position, destinationMapCoords.Position, currentMapCoords.MapId))
                        {
                            var goalAngle = Angle.FromWorldVec(diff);
                            _rotateToFace.TryRotateTo(localWeapon, goalAngle, 0f, Angle.FromDegrees(1), float.MaxValue, weaponXform);
                        }
                    }
                }
            }

            var weaponX = Transform(localWeapon);
            var targetPos = targetCoords.ToMap(EntityManager, _xform);

            if (targetPos.MapId != weaponXform.MapID)
                continue;

            var weaponPos = _xform.GetWorldPosition(weaponXform);

            var direction = (targetPos.Position - weaponPos);
            var distance = direction.Length();
            if (distance <= 0)
                continue;

            direction = Vector2.Normalize(direction);

            var ray = new CollisionRay(weaponPos, direction, collisionMask: (int)(CollisionGroup.Opaque | CollisionGroup.Impassable));
            var rayCastResults = _physics.IntersectRay(weaponXform.MapID, ray, distance, localWeapon, false).ToList();

            if (rayCastResults.Count == 0)
            {
                _gun.AttemptShoot(localWeapon, localWeapon, gun, targetCoords);
            }
        }
    }
}

public sealed class FireControllableStatusReportEvent : EntityEventArgs
{
    public List<(string type, string content)> StatusReports = new();
}
