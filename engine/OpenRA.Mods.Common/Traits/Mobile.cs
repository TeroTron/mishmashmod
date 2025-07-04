#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Unit is able to move.")]
	public class MobileInfo : PausableConditionalTraitInfo, IMoveInfo, IPositionableInfo, IFacingInfo, IActorPreviewInitInfo,
		IEditorActorOptions
	{
		[LocomotorReference]
		[FieldLoader.Require]
		[Desc("Which Locomotor does this trait use. Must be defined on the World actor.")]
		public readonly string Locomotor = null;

		public readonly WAngle InitialFacing = WAngle.Zero;

		[Desc("Speed at which the actor turns.")]
		public readonly WAngle TurnSpeed = new(512);

		public readonly int Speed = 1;

		[Desc("If set to true, this unit will always turn in place instead of following a curved trajectory (like infantry).")]
		public readonly bool AlwaysTurnInPlace = false;

		[Desc("If set to true, this unit won't stop to turn, it will turn while moving instead.")]
		public readonly bool TurnsWhileMoving = false;

		[CursorReference]
		[Desc("Cursor to display when a move order can be issued at target location.")]
		public readonly string Cursor = "move";

		[CursorReference(dictionaryReference: LintDictionaryReference.Values)]
		[Desc("Cursor overrides to display for specific terrain types.",
			"A dictionary of [terrain type]: [cursor name].")]
		public readonly Dictionary<string, string> TerrainCursors = [];

		[CursorReference]
		[Desc("Cursor to display when a move order cannot be issued at target location.")]
		public readonly string BlockedCursor = "move-blocked";

		[VoiceReference]
		public readonly string Voice = "Action";

		[Desc("Color to use for the target line for regular move orders.")]
		public readonly Color TargetLineColor = Color.Green;

		[Desc("Facing to use for actor previews (map editor, color picker, etc)")]
		public readonly WAngle PreviewFacing = new(384);

		[Desc("Display order for the facing slider in the map editor")]
		public readonly int EditorFacingDisplayOrder = 3;

		[Desc("Can move backward if possible")]
		public readonly bool CanMoveBackward = false;

		[Desc("After how many ticks the actor will turn forward during backoff.",
			"If set to -1 the unit will be allowed to move backwards without time limit.")]
		public readonly int BackwardDuration = 40;

		[Desc("Actor will only try to move backwards when the path (in cells) is shorter than this value.",
			"If set to -1 the unit will be allowed to move backwards without range limit.")]
		public readonly int MaxBackwardCells = 15;

		[Desc("Can the actor turn, even when the trait is disabled")]
		public readonly bool CanTurnWhileDisabled = false;

		[ConsumedConditionReference]
		[Desc("Boolean expression defining the condition under which the regular (non-force) move cursor is disabled.")]
		public readonly BooleanExpression RequireForceMoveCondition = null;

		[ConsumedConditionReference]
		[Desc("Boolean expression defining the condition under which this actor cannot be nudged by other actors.")]
		public readonly BooleanExpression ImmovableCondition = null;

		[Desc("The distance from the edge of a cell over which the actor will adjust its tilt when moving between cells with different ramp types.",
			"-1 means that the actor does not tilt on slopes.")]
		public readonly WDist TerrainOrientationAdjustmentMargin = new(-1);

		IEnumerable<ActorInit> IActorPreviewInitInfo.ActorPreviewInits(ActorInfo ai, ActorPreviewType type)
		{
			yield return new FacingInit(PreviewFacing);
		}

		public Color GetTargetLineColor() { return TargetLineColor; }

		public override object Create(ActorInitializer init) { return new Mobile(init, this); }

		public LocomotorInfo LocomotorInfo { get; private set; }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			var locomotorInfos = rules.Actors[SystemActors.World].TraitInfos<LocomotorInfo>()
				.Where(li => li.Name == Locomotor).ToList();
			if (locomotorInfos.Count == 0)
				throw new YamlException($"A locomotor named '{Locomotor}' doesn't exist.");
			else if (locomotorInfos.Count > 1)
				throw new YamlException($"There is more than one locomotor named '{Locomotor}'.");

			LocomotorInfo = locomotorInfos[0];

			// We need to reset the reference to the locomotor between each worlds, otherwise we are reference the previous state.
			locomotor = null;

			base.RulesetLoaded(rules, ai);
		}

		public WAngle GetInitialFacing() { return InitialFacing; }

		// initialized and used by CanEnterCell
		Locomotor locomotor;

		/// <summary>
		/// Note: If the target <paramref name="cell"/> has any free subcell, the value of <paramref name="subCell"/> is ignored.
		/// </summary>
		public bool CanEnterCell(World world, Actor self, CPos cell,
			SubCell subCell = SubCell.FullCell, Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All)
		{
			// PERF: Avoid repeated trait queries on the hot path
			locomotor ??= world.WorldActor.TraitsImplementing<Locomotor>()
				   .SingleOrDefault(l => l.Info.Name == Locomotor);

			return locomotor.MovementCostToEnterCell(
				self, cell, check, ignoreActor, false, subCell) != PathGraph.MovementCostForUnreachableCell;
		}

		public bool CanStayInCell(World world, CPos cell)
		{
			// PERF: Avoid repeated trait queries on the hot path
			locomotor ??= world.WorldActor.TraitsImplementing<Locomotor>()
				   .SingleOrDefault(l => l.Info.Name == Locomotor);

			if (cell.Layer == CustomMovementLayerType.Tunnel)
				return false;

			return locomotor.CanStayInCell(cell);
		}

		public IReadOnlyDictionary<CPos, SubCell> OccupiedCells(ActorInfo info, CPos location, SubCell subCell = SubCell.Any)
		{
			return new Dictionary<CPos, SubCell>() { { location, subCell } };
		}

		bool IOccupySpaceInfo.SharesCell => LocomotorInfo.SharesCell;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorSlider("Facing", EditorFacingDisplayOrder, 0, 1023, 8,
				actor =>
				{
					var init = actor.GetInitOrDefault<FacingInit>(this);
					return (init?.Value ?? InitialFacing).Angle;
				},
				(actor, value) => actor.ReplaceInit(new FacingInit(new WAngle((int)value))));
		}
	}

	public class Mobile : PausableConditionalTrait<MobileInfo>, IIssueOrder, IResolveOrder, IOrderVoice, IPositionable, IMove, ITick, ICreationActivity,
		IFacing, IDeathActorInitModifier, INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyBlockingMove, IActorPreviewInitModifier, INotifyBecomingIdle
	{
		readonly Actor self;
		readonly Lazy<IEnumerable<int>> speedModifiers;
		public readonly IEnumerable<int> TurnSpeedModifiers;

		readonly bool returnToCellOnCreation;
		readonly bool returnToCellOnCreationRecalculateSubCell = true;
		readonly int creationActivityDelay;
		readonly CPos[] creationRallypoint;

		#region IMove CurrentMovementTypes
		MovementType movementTypes;
		public MovementType CurrentMovementTypes
		{
			get => movementTypes;

			set
			{
				var oldValue = movementTypes;
				movementTypes = value;
				if (value != oldValue)
				{
					self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
					foreach (var n in notifyMoving)
						n.MovementTypeChanged(self, value);
				}
			}
		}
		#endregion

		WRot terrainRampOrientation = WRot.None;
		WAngle oldFacing;
		WRot orientation;
		WPos oldPos;
		public SubCell FromSubCell, ToSubCell;

		INotifyCustomLayerChanged[] notifyCustomLayerChanged;
		INotifyCenterPositionChanged[] notifyCenterPositionChanged;
		INotifyMoving[] notifyMoving;
		INotifyFinishedMoving[] notifyFinishedMoving;
		IWrapMove[] moveWrappers;
		bool requireForceMove;

		public bool IsImmovable { get; private set; }
		public bool TurnToMove;
		public bool IsBlocking { get; private set; }

		public bool IsMovingBetweenCells => FromCell != ToCell;
		public MoveResult MoveResult { get; set; }

		#region IFacing

		[Sync]
		public WAngle Facing
		{
			get => orientation.Yaw;
			set => orientation = orientation.WithYaw(value);
		}

		public WRot Orientation => orientation.Rotate(terrainRampOrientation);

		public WAngle TurnSpeed => Info.TurnSpeed;

		#endregion

		[Sync]
		public CPos FromCell { get; private set; }

		[Sync]
		public CPos ToCell { get; private set; }

		public Locomotor Locomotor { get; private set; }

		public IPathFinder PathFinder { get; private set; }

		#region IOccupySpace

		[Sync]
		public WPos CenterPosition { get; private set; }

		public CPos TopLeft => ToCell;

		public (CPos, SubCell)[] OccupiedCells()
		{
			if (FromCell == ToCell)
				return [(FromCell, FromSubCell)];

			// HACK: Should be fixed properly, see https://github.com/OpenRA/OpenRA/pull/17292 for an explanation
			if (Info.LocomotorInfo.SharesCell)
				return [(ToCell, ToSubCell)];

			return [(FromCell, FromSubCell), (ToCell, ToSubCell)];
		}
		#endregion

		public Mobile(ActorInitializer init, MobileInfo info)
			: base(info)
		{
			self = init.Self;

			speedModifiers = Exts.Lazy(() => self.TraitsImplementing<ISpeedModifier>().ToArray().Select(x => x.GetSpeedModifier()));
			TurnSpeedModifiers = self.TraitsImplementing<ITurnSpeedModifier>().ToArray().Select(x => x.GetTurnSpeedModifier());

			ToSubCell = FromSubCell = info.LocomotorInfo.SharesCell ? init.World.Map.Grid.DefaultSubCell : SubCell.FullCell;

			var subCellInit = init.GetOrDefault<SubCellInit>();
			if (subCellInit != null)
			{
				FromSubCell = ToSubCell = subCellInit.Value;
				returnToCellOnCreationRecalculateSubCell = false;
			}

			var locationInit = init.GetOrDefault<LocationInit>();
			if (locationInit != null)
			{
				FromCell = ToCell = locationInit.Value;
				SetCenterPosition(self, init.World.Map.CenterOfSubCell(FromCell, FromSubCell));
			}

			Facing = oldFacing = init.GetValue<FacingInit, WAngle>(info.InitialFacing);

			// Sets the initial center position
			// Unit will move into the cell grid (defined by LocationInit) as its initial activity
			var centerPositionInit = init.GetOrDefault<CenterPositionInit>();
			if (centerPositionInit != null)
			{
				oldPos = centerPositionInit.Value;
				SetCenterPosition(self, oldPos);
				returnToCellOnCreation = true;
			}

			creationActivityDelay = init.GetValue<CreationActivityDelayInit, int>(0);
			creationRallypoint = init.GetOrDefault<RallyPointInit>()?.Value;
		}

		protected override void Created(Actor self)
		{
			notifyCustomLayerChanged = self.TraitsImplementing<INotifyCustomLayerChanged>().ToArray();
			notifyCenterPositionChanged = self.TraitsImplementing<INotifyCenterPositionChanged>().ToArray();
			notifyMoving = self.TraitsImplementing<INotifyMoving>().ToArray();
			notifyFinishedMoving = self.TraitsImplementing<INotifyFinishedMoving>().ToArray();
			moveWrappers = self.TraitsImplementing<IWrapMove>().ToArray();
			PathFinder = self.World.WorldActor.Trait<IPathFinder>();
			Locomotor = self.World.WorldActor.TraitsImplementing<Locomotor>()
				.Single(l => l.Info.Name == Info.Locomotor);

			base.Created(self);
		}

		void ITick.Tick(Actor self)
		{
			UpdateMovement();
		}

		public void UpdateMovement()
		{
			var newMovementTypes = MovementType.None;
			if ((oldPos - CenterPosition).HorizontalLengthSquared != 0)
				newMovementTypes |= MovementType.Horizontal;

			if (oldPos.Z != CenterPosition.Z)
				newMovementTypes |= MovementType.Vertical;

			if (oldFacing != Facing)
				newMovementTypes |= MovementType.Turn;

			CurrentMovementTypes = newMovementTypes;

			oldPos = CenterPosition;
			oldFacing = Facing;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			self.World.AddToMaps(self, this);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			self.World.RemoveFromMaps(self, this);
		}

		protected override void TraitEnabled(Actor self)
		{
			self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}

		protected override void TraitDisabled(Actor self)
		{
			self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}

		protected override void TraitResumed(Actor self)
		{
			self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}

		protected override void TraitPaused(Actor self)
		{
			self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}

		#region Local misc stuff

		public CPos? GetAdjacentCell(CPos nextCell, Func<CPos, bool> preferToAvoid = null)
		{
			var availCells = new List<CPos>();
			var notStupidCells = new List<CPos>();
			foreach (var direction in CVec.Directions)
			{
				var p = ToCell + direction;
				if (CanEnterCell(p) && CanStayInCell(p) && (preferToAvoid == null || !preferToAvoid(p)))
					availCells.Add(p);
				else if (p != nextCell && p != ToCell)
					notStupidCells.Add(p);
			}

			CPos? newCell = null;
			if (availCells.Count > 0)
				newCell = availCells.Random(self.World.SharedRandom);
			else
			{
				var cellInfo = notStupidCells
					.SelectMany(c => self.World.ActorMap.GetActorsAt(c).Where(IsMovable),
						(c, a) => new { Cell = c, Actor = a })
					.RandomOrDefault(self.World.SharedRandom);
				if (cellInfo != null)
					newCell = cellInfo.Cell;
			}

			return newCell;
		}

		static bool IsMovable(Actor otherActor)
		{
			if (!otherActor.IsIdle)
				return false;

			var mobile = otherActor.TraitOrDefault<Mobile>();
			if (mobile == null || mobile.IsTraitDisabled || mobile.IsTraitPaused || mobile.IsImmovable)
				return false;

			return true;
		}

		public bool IsLeaving()
		{
			if (CurrentMovementTypes.HasMovementType(MovementType.Horizontal))
				return true;

			if (CurrentMovementTypes.HasMovementType(MovementType.Turn))
				return TurnToMove;

			return false;
		}

		public bool CanInteractWithGroundLayer(Actor self)
		{
			// TODO: Think about extending this to support arbitrary layer-layer checks
			// in a way that is compatible with the other IMove types.
			// This would then allow us to e.g. have units attack other units inside tunnels.
			if (ToCell.Layer == 0)
				return true;

			var layer = self.World.GetCustomMovementLayers()[ToCell.Layer];
			return layer == null || layer.InteractsWithDefaultLayer;
		}

		#endregion

		#region IPositionable

		// Returns a valid sub-cell
		public SubCell GetValidSubCell(SubCell preferred = SubCell.Any)
		{
			// Try same sub-cell
			if (preferred == SubCell.Any)
				preferred = FromSubCell;

			// Fix sub-cell assignment
			if (Info.LocomotorInfo.SharesCell)
			{
				if (preferred <= SubCell.FullCell)
					return self.World.Map.Grid.DefaultSubCell;
			}
			else
			{
				if (preferred != SubCell.FullCell)
					return SubCell.FullCell;
			}

			return preferred;
		}

		// Sets the location (fromCell, toCell, FromSubCell, ToSubCell) and CenterPosition
		public void SetPosition(Actor self, CPos cell, SubCell subCell = SubCell.Any)
		{
			subCell = GetValidSubCell(subCell);
			SetLocation(cell, subCell, cell, subCell);

			var position = cell.Layer == 0 ? self.World.Map.CenterOfCell(cell) :
				self.World.GetCustomMovementLayers()[cell.Layer].CenterOfCell(cell);

			position += self.World.Map.Grid.OffsetOfSubCell(subCell);
			position -= new WVec(0, 0, self.World.Map.DistanceAboveTerrain(position).Length);

			SetCenterPosition(self, position);
			FinishedMoving(self);
		}

		// Sets the location (fromCell, toCell, FromSubCell, ToSubCell) and CenterPosition
		public void SetPosition(Actor self, WPos pos)
		{
			var cell = self.World.Map.CellContaining(pos);
			SetLocation(cell, FromSubCell, cell, FromSubCell);
			SetCenterPosition(self, self.World.Map.CenterOfSubCell(cell, FromSubCell) + new WVec(0, 0, self.World.Map.DistanceAboveTerrain(pos).Length));
			FinishedMoving(self);
		}

		// Sets only the CenterPosition
		public void SetCenterPosition(Actor self, WPos pos)
		{
			CenterPosition = pos;
			self.World.UpdateMaps(self, this);

			var map = self.World.Map;
			SetTerrainRampOrientation(map.TerrainOrientation(map.CellContaining(pos)));

			// The first time SetCenterPosition is called is in the constructor before creation, so we need a null check here as well
			if (notifyCenterPositionChanged == null)
				return;

			foreach (var n in notifyCenterPositionChanged)
				n.CenterPositionChanged(self, FromCell.Layer, ToCell.Layer);
		}

		public void SetTerrainRampOrientation(WRot orientation)
		{
			if (Info.TerrainOrientationAdjustmentMargin.Length >= 0)
				terrainRampOrientation = orientation;
		}

		public bool IsLeavingCell(CPos location, SubCell subCell = SubCell.Any)
		{
			return ToCell != location && FromCell == location
				&& (subCell == SubCell.Any || FromSubCell == subCell || subCell == SubCell.FullCell || FromSubCell == SubCell.FullCell);
		}

		public SubCell GetAvailableSubCell(CPos a, SubCell preferredSubCell = SubCell.Any, Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All)
		{
			return Locomotor.GetAvailableSubCell(self, a, check, preferredSubCell, ignoreActor);
		}

		public bool CanExistInCell(CPos cell)
		{
			return Locomotor.MovementCostForCell(cell) != PathGraph.MovementCostForUnreachableCell;
		}

		public bool CanEnterCell(CPos cell, Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All)
		{
			return Info.CanEnterCell(self.World, self, cell, ToSubCell, ignoreActor, check);
		}

		public bool CanStayInCell(CPos cell)
		{
			return Info.CanStayInCell(self.World, cell);
		}

		#endregion

		#region Local IPositionable-related

		// Sets only the location (fromCell, toCell, FromSubCell, ToSubCell)
		public void SetLocation(CPos from, SubCell fromSub, CPos to, SubCell toSub)
		{
			if (FromCell == from && ToCell == to && FromSubCell == fromSub && ToSubCell == toSub)
				return;

			RemoveInfluence();
			FromCell = from;
			ToCell = to;
			FromSubCell = fromSub;
			ToSubCell = toSub;
			AddInfluence();
			IsBlocking = false;

			// Most custom layer conditions are added/removed when starting the transition between layers.
			if (ToCell.Layer != FromCell.Layer)
				foreach (var n in notifyCustomLayerChanged)
					n.CustomLayerChanged(self, FromCell.Layer, ToCell.Layer);
		}

		public void FinishedMoving(Actor self)
		{
			// Need to check both fromCell and toCell because FinishedMoving is called multiple times during the move
			if (FromCell.Layer == ToCell.Layer)
				foreach (var n in notifyFinishedMoving)
					n.FinishedMoving(self, FromCell.Layer, ToCell.Layer);

			// Only crush actors on having landed
			if (!self.IsAtGroundLevel())
				return;

			CrushAction(self, (notifyCrushed) => notifyCrushed.OnCrush);
		}

		void CrushAction(Actor self, Func<INotifyCrushed, Action<Actor, Actor, BitSet<CrushClass>>> action)
		{
			var crushables = self.World.ActorMap.GetActorsAt(ToCell, ToSubCell).Where(a => a != self)
				.SelectMany(a => a.Crushables.Select(t => new TraitPair<ICrushable>(a, t)));

			// Only crush actors that are on the ground level
			foreach (var crushable in crushables)
				if (crushable.Trait.CrushableBy(crushable.Actor, self, Info.LocomotorInfo.Crushes) && crushable.Actor.IsAtGroundLevel())
					foreach (var notifyCrushed in crushable.Actor.TraitsImplementing<INotifyCrushed>())
						action(notifyCrushed)(crushable.Actor, self, Info.LocomotorInfo.Crushes);
		}

		public void AddInfluence()
		{
			if (self.IsInWorld)
				self.World.ActorMap.AddInfluence(self, this);
		}

		public void RemoveInfluence()
		{
			if (self.IsInWorld)
				self.World.ActorMap.RemoveInfluence(self, this);
		}

		#endregion

		#region IMove

		Activity WrapMove(Activity inner)
		{
			var moveWrapper = moveWrappers.FirstEnabledTraitOrDefault();
			if (moveWrapper != null)
				return moveWrapper.WrapMove(inner);

			return inner;
		}

		public Activity MoveTo(CPos cell, int nearEnough = 0, Actor ignoreActor = null,
			bool evaluateNearestMovableCell = false, Color? targetLineColor = null)
		{
			return WrapMove(new Move(self, cell, WDist.FromCells(nearEnough), ignoreActor, evaluateNearestMovableCell, targetLineColor));
		}

		public Activity MoveWithinRange(in Target target, WDist range,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			return WrapMove(new MoveWithinRange(self, target, WDist.Zero, range, initialTargetPosition, targetLineColor));
		}

		public Activity MoveWithinRange(in Target target, WDist minRange, WDist maxRange,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			return WrapMove(new MoveWithinRange(self, target, minRange, maxRange, initialTargetPosition, targetLineColor));
		}

		public Activity MoveFollow(Actor self, in Target target, WDist minRange, WDist maxRange,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			return WrapMove(new Follow(self, target, minRange, maxRange, initialTargetPosition, targetLineColor));
		}

		public Activity ReturnToCell(Actor self)
		{
			return new ReturnToCellActivity(self);
		}

		public class ReturnToCellActivity : Activity
		{
			readonly Mobile mobile;
			readonly bool recalculateSubCell;

			CPos cell;
			SubCell subCell;
			WPos pos;
			readonly int delay;

			public ReturnToCellActivity(Actor self, int delay = 0, bool recalculateSubCell = false)
			{
				ActivityType = ActivityType.Move;
				mobile = self.Trait<Mobile>();
				IsInterruptible = false;
				this.delay = delay;
				this.recalculateSubCell = recalculateSubCell;
			}

			protected override void OnFirstRun(Actor self)
			{
				pos = self.CenterPosition;
				if (self.World.Map.DistanceAboveTerrain(pos) > WDist.Zero && self.TraitOrDefault<Parachutable>() != null)
					QueueChild(new Parachute(self));
			}

			public override bool Tick(Actor self)
			{
				pos = self.CenterPosition;
				cell = mobile.ToCell;
				subCell = mobile.ToSubCell;

				if (recalculateSubCell)
					subCell = mobile.Info.LocomotorInfo.SharesCell ? self.World.ActorMap.FreeSubCell(cell, subCell, a => a != self) : SubCell.FullCell;

				// TODO: solve/reduce cell is full problem
				if (subCell == SubCell.Invalid)
					subCell = self.World.Map.Grid.DefaultSubCell;

				// Reserve the exit cell
				mobile.SetPosition(self, cell, subCell);
				mobile.SetCenterPosition(self, pos);

				if (delay > 0)
					QueueChild(new Wait(delay));

				QueueChild(mobile.LocalMove(self, pos, self.World.Map.CenterOfSubCell(cell, subCell)));
				return true;
			}
		}

		public Activity MoveToTarget(Actor self, in Target target,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			if (target.Type == TargetType.Invalid)
				return null;

			return WrapMove(new MoveAdjacentTo(self, target, initialTargetPosition, targetLineColor));
		}

		public Activity MoveIntoTarget(Actor self, in Target target)
		{
			if (target.Type == TargetType.Invalid)
				return null;

			// Activity cancels if the target moves by more than half a cell
			// to avoid problems with the cell grid
			return WrapMove(new LocalMoveIntoTarget(self, target, new WDist(512)));
		}

		public Activity MoveOntoTarget(Actor self, in Target target, in WVec offset, WAngle? facing, Color? targetLineColor = null)
		{
			return WrapMove(new MoveOntoAndTurn(self, target, offset, facing, targetLineColor));
		}

		public Activity LocalMove(Actor self, WPos fromPos, WPos toPos)
		{
			return WrapMove(LocalMove(self, fromPos, toPos, self.Location));
		}

		public int EstimatedMoveDuration(Actor self, WPos fromPos, WPos toPos)
		{
			var speed = MovementSpeedForCell(self.Location);
			return speed > 0 ? (toPos - fromPos).Length / speed : 0;
		}

		public CPos NearestMoveableCell(CPos target)
		{
			// Limit search to a radius of 10 tiles
			return NearestMoveableCell(target, 1, 10);
		}

		public bool CanEnterTargetNow(Actor self, in Target target)
		{
			if (target.Type == TargetType.FrozenActor && !target.FrozenActor.IsValid)
				return false;

			return self.Location == self.World.Map.CellContaining(target.CenterPosition) || Util.AdjacentCells(self.World, target).Any(c => c == self.Location);
		}

		#endregion

		#region Local IMove-related

		public int MovementSpeedForCell(CPos cell)
		{
			var terrainSpeed = Locomotor.MovementSpeedForCell(cell);
			var modifiers = speedModifiers.Value.Append(terrainSpeed);

			return Util.ApplyPercentageModifiers(Info.Speed, modifiers);
		}

		public CPos NearestMoveableCell(CPos target, int minRange, int maxRange)
		{
			// HACK: This entire method is a hack, and needs to be replaced with
			// a proper path search that can account for movement layer transitions.
			// HACK: Work around code that blindly tries to move to cells in invalid movement layers.
			// This will need to change (by removing this method completely as above) before we can
			// properly support user-issued orders on to elevated bridges or other interactable custom layers
			if (target.Layer != 0)
				target = new CPos(target.X, target.Y);

			if (target == self.Location && CanStayInCell(target))
				return target;

			if (CanEnterCell(target, check: BlockedByActor.Immovable) && CanStayInCell(target))
				return target;

			foreach (var tile in self.World.Map.FindTilesInAnnulus(target, minRange, maxRange))
				if (CanEnterCell(tile, check: BlockedByActor.Immovable) && CanStayInCell(tile))
					return tile;

			// Couldn't find a cell
			return target;
		}

		public CPos NearestCell(CPos target, Func<CPos, bool> check, int minRange, int maxRange)
		{
			if (check(target))
				return target;

			foreach (var tile in self.World.Map.FindTilesInAnnulus(target, minRange, maxRange))
				if (check(tile))
					return tile;

			// Couldn't find a cell
			return target;
		}

		public void EnteringCell(Actor self)
		{
			// Only crush actors on having landed
			if (!self.IsAtGroundLevel())
				return;

			CrushAction(self, (notifyCrushed) => notifyCrushed.WarnCrush);
		}

		public Activity MoveTo(Func<BlockedByActor, (bool AlreadyAtDestination, List<CPos> Path)> pathFunc) { return new Move(self, pathFunc); }

		Activity LocalMove(Actor self, WPos fromPos, WPos toPos, CPos cell)
		{
			var speed = MovementSpeedForCell(cell);
			var length = speed > 0 ? (toPos - fromPos).Length / speed : 0;

			var delta = toPos - fromPos;
			var facing = delta.HorizontalLengthSquared != 0 ? delta.Yaw : Facing;

			return new Drag(self, fromPos, toPos, length, facing);
		}

		CPos? ClosestGroundCell()
		{
			// Creating a new CPos serves to reset a potential custom layer
			var above = new CPos(TopLeft.X, TopLeft.Y);
			if (CanEnterCell(above))
				return above;

			var path = PathFinder.FindPathToTargetCellByPredicate(
				self, [self.Location], loc => loc.Layer == 0 && CanEnterCell(loc), BlockedByActor.All);

			if (path.Count > 0)
				return path[0];

			return null;
		}

		#endregion

		void IActorPreviewInitModifier.ModifyActorPreviewInit(Actor self, TypeDictionary inits)
		{
			if (!inits.Contains<DynamicFacingInit>() && !inits.Contains<FacingInit>())
				inits.Add(new DynamicFacingInit(() => Facing));
		}

		void IDeathActorInitModifier.ModifyDeathActorInit(Actor self, TypeDictionary init)
		{
			init.Add(new FacingInit(Facing));

			// Allows the husk to drag to its final position
			if (CanEnterCell(self.Location, self, BlockedByActor.Stationary))
				init.Add(new HuskSpeedInit(MovementSpeedForCell(self.Location)));
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			if (self.Location.Layer == 0)
			{
				// Make sure that units aren't left idling in a transit-only cell
				// HACK: activities should be making sure that this can't happen in the first place!
				if (!Locomotor.CanStayInCell(self.Location))
					self.QueueActivity(MoveTo(self.Location, evaluateNearestMovableCell: true));
				return;
			}

			var cml = self.World.GetCustomMovementLayers()[self.Location.Layer];
			if (!cml.ReturnToGroundLayerOnIdle)
				return;

			var moveTo = ClosestGroundCell();
			if (moveTo != null)
				self.QueueActivity(MoveTo(moveTo.Value, 0));
		}

		void INotifyBlockingMove.OnNotifyBlockingMove(Actor self, Actor blocking)
		{
			if (!self.AppearsFriendlyTo(blocking))
				return;

			if (self.IsIdle)
			{
				self.QueueActivity(false, new Nudge(blocking));
				return;
			}

			IsBlocking = true;
		}

		public override IEnumerable<VariableObserver> GetVariableObservers()
		{
			foreach (var observer in base.GetVariableObservers())
				yield return observer;

			if (Info.RequireForceMoveCondition != null)
				yield return new VariableObserver(RequireForceMoveConditionChanged, Info.RequireForceMoveCondition.Variables);

			if (Info.ImmovableCondition != null)
				yield return new VariableObserver(ImmovableConditionChanged, Info.ImmovableCondition.Variables);
		}

		void RequireForceMoveConditionChanged(Actor self, IReadOnlyDictionary<string, int> conditions)
		{
			requireForceMove = Info.RequireForceMoveCondition.Evaluate(conditions);
		}

		void ImmovableConditionChanged(Actor self, IReadOnlyDictionary<string, int> conditions)
		{
			var wasImmovable = IsImmovable;
			IsImmovable = Info.ImmovableCondition.Evaluate(conditions);
			if (wasImmovable != IsImmovable)
				self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				if (!IsTraitDisabled)
					yield return new MoveOrderTargeter(self, this);
			}
		}

		// Note: Returns a valid order even if the unit can't move to the target
		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order is MoveOrderTargeter)
				return new Order("Move", self, target, queued);

			return null;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (IsTraitDisabled)
				return;

			if (order.OrderString == "Move")
			{
				if (!order.Target.IsValidFor(self))
					return;

				var cell = self.World.Map.Clamp(this.self.World.Map.CellContaining(order.Target.CenterPosition));
				if (!Info.LocomotorInfo.MoveIntoShroud && !self.Owner.Shroud.IsExplored(cell))
					return;

				self.QueueActivity(order.Queued, WrapMove(new Move(self, cell, WDist.FromCells(8), null, true, Info.TargetLineColor)));
				self.ShowTargetLines();
			}

			// TODO: This should only cancel activities queued by this trait
			else if (order.OrderString == "Stop")
				self.CancelActivity();
			else if (order.OrderString == "Scatter")
			{
				self.QueueActivity(order.Queued, new Nudge(self));
				self.ShowTargetLines();
			}
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			if (IsTraitDisabled)
				return null;

			switch (order.OrderString)
			{
				case "Move":
					if (!Info.LocomotorInfo.MoveIntoShroud && order.Target.Type != TargetType.Invalid)
					{
						var cell = self.World.Map.CellContaining(order.Target.CenterPosition);
						if (!self.Owner.Shroud.IsExplored(cell))
							return null;
					}

					return Info.Voice;
				case "Scatter":
				case "Stop":
					return Info.Voice;
				default:
					return null;
			}
		}

		public class LeaveProductionActivity : Activity
		{
			readonly Mobile mobile;
			readonly int delay;
			readonly CPos[] rallyPoint;
			readonly ReturnToCellActivity returnToCell;

			public LeaveProductionActivity(Actor self, int delay, CPos[] rallyPoint, ReturnToCellActivity returnToCell)
			{
				mobile = self.Trait<Mobile>();
				this.delay = delay;
				this.rallyPoint = rallyPoint;
				this.returnToCell = returnToCell;
			}

			protected override void OnFirstRun(Actor self)
			{
				// It is vital that ReturnToCell is queued first as it needs the power to intercept a possible cancellation of this activity.
				if (returnToCell != null)
					QueueChild(returnToCell);
				else if (delay > 0)
					QueueChild(new Wait(delay));

				if (rallyPoint != null)
					foreach (var cell in rallyPoint)
						QueueChild(new AttackMoveActivity(self, () => mobile.MoveTo(cell, 1, evaluateNearestMovableCell: true, targetLineColor: Color.OrangeRed)));
			}

			public override IEnumerable<Target> GetTargets(Actor self)
			{
				if (ChildActivity != null)
					return ChildActivity.GetTargets(self);

				return Target.None;
			}

			public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
			{
				var a = ChildActivity;
				for (; a != null; a = a.NextActivity)
					if (!a.IsCanceling)
						foreach (var n in a.TargetLineNodes(self))
							yield return n;
			}
		}

		Activity ICreationActivity.GetCreationActivity()
		{
			if (returnToCellOnCreation || creationRallypoint != null || creationActivityDelay > 0)
				return new LeaveProductionActivity(self, creationActivityDelay, creationRallypoint,
					returnToCellOnCreation ? new ReturnToCellActivity(self, creationActivityDelay, returnToCellOnCreationRecalculateSubCell) : null);

			return null;
		}

		sealed class MoveOrderTargeter : IOrderTargeter
		{
			readonly Mobile mobile;
			readonly LocomotorInfo locomotorInfo;
			readonly bool rejectMove;
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers)
			{
				// Always prioritise orders over selecting other peoples actors or own actors that are already selected
				if (target.Type == TargetType.Actor && (target.Actor.Owner != self.Owner || self.World.Selection.Contains(target.Actor)))
					return true;

				return modifiers.HasModifier(TargetModifiers.ForceMove);
			}

			public MoveOrderTargeter(Actor self, Mobile unit)
			{
				mobile = unit;
				locomotorInfo = mobile.Info.LocomotorInfo;
				rejectMove = !self.AcceptsOrder("Move");
			}

			public string OrderID => "Move";
			public int OrderPriority => 4;
			public bool IsQueued { get; private set; }

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (rejectMove || target.Type != TargetType.Terrain || (mobile.requireForceMove && !modifiers.HasModifier(TargetModifiers.ForceMove)))
					return false;

				var location = self.World.Map.CellContaining(target.CenterPosition);
				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);

				var explored = self.Owner.Shroud.IsExplored(location);

				if (mobile.IsTraitPaused
					|| !self.World.Map.Contains(location)
					|| (!explored && !locomotorInfo.MoveIntoShroud)
					|| (explored && mobile.Locomotor.MovementCostForCell(location) == PathGraph.MovementCostForUnreachableCell))
					cursor = mobile.Info.BlockedCursor;
				else if (!explored || !mobile.Info.TerrainCursors.TryGetValue(self.World.Map.GetTerrainInfo(location).Type, out cursor))
					cursor = mobile.Info.Cursor;

				return true;
			}
		}
	}
}
