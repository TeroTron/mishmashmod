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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class Move : Activity
	{
		public WAngle ActorFacingModifier { get; private set; }
		readonly Mobile mobile;
		readonly WDist nearEnough;
		readonly Func<BlockedByActor, (bool AlreadyAtDestination, List<CPos> Path)> getPath;
		readonly Actor ignoreActor;
		readonly Color? targetLineColor;

		static readonly BlockedByActor[] PathSearchOrder =
		[
			BlockedByActor.All,
			BlockedByActor.Stationary,
			BlockedByActor.Immovable,
			BlockedByActor.None
		];

		int carryoverProgress;
		int lastMovePartCompletedTick;

		bool alreadyAtDestination;
		List<CPos> path;
		CPos? destination;
		int startTicks;
		bool hadNoPath;

		// For dealing with blockers
		bool hasWaited;
		int waitTicksRemaining;

		// To work around queued activity issues while minimizing changes to legacy behaviour
		readonly bool evaluateNearestMovableCell;

		// Scriptable move order
		// Ignores lane bias
		public Move(Actor self, CPos destination, Color? targetLineColor = null)
		{
			ActivityType = ActivityType.Move;

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			getPath = check =>
			{
				if (mobile.ToCell == destination)
					return (true, PathFinder.NoPath);

				return (false, mobile.PathFinder.FindPathToTargetCell(
					self, [mobile.ToCell], destination, check, laneBias: false));
			};

			this.destination = destination;
			this.targetLineColor = targetLineColor;
			nearEnough = WDist.Zero;
		}

		public Move(Actor self, CPos destination, WDist nearEnough, Actor ignoreActor = null, bool evaluateNearestMovableCell = false,
			Color? targetLineColor = null)
		{
			ActivityType = ActivityType.Move;

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			getPath = check =>
			{
				if (!this.destination.HasValue)
					return (false, PathFinder.NoPath);

				if (mobile.ToCell == this.destination.Value)
					return (true, PathFinder.NoPath);

				return (false, mobile.PathFinder.FindPathToTargetCell(
					self, [mobile.ToCell], this.destination.Value, check, ignoreActor: ignoreActor));
			};

			// Note: Will be recalculated from OnFirstRun if evaluateNearestMovableCell is true
			this.destination = destination;

			this.nearEnough = nearEnough;
			this.ignoreActor = ignoreActor;
			this.evaluateNearestMovableCell = evaluateNearestMovableCell;
			this.targetLineColor = targetLineColor;
		}

		public Move(Actor self, Func<BlockedByActor, (bool AlreadyAtDestination, List<CPos> Path)> getPath, Color? targetLineColor = null)
		{
			ActivityType = ActivityType.Move;

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			this.getPath = getPath;

			destination = null;
			nearEnough = WDist.Zero;
			this.targetLineColor = targetLineColor;
		}

		(bool AlreadyAtDestination, List<CPos> Path) EvalPath(BlockedByActor check)
		{
			var (alreadyAtDestination, path) = getPath(check);
			path = path.TakeWhile(a => a != mobile.ToCell).ToList();
			return (alreadyAtDestination, path);
		}

		protected override void OnFirstRun(Actor self)
		{
			startTicks = self.World.WorldTick;
			mobile.MoveResult = MoveResult.InProgress;

			if (evaluateNearestMovableCell && destination.HasValue)
			{
				var movableDestination = mobile.NearestMoveableCell(destination.Value);
				destination = mobile.CanEnterCell(movableDestination, check: BlockedByActor.Immovable) ? movableDestination : null;
			}

			// TODO: Change this to BlockedByActor.Stationary after improving the local avoidance behaviour
			foreach (var check in PathSearchOrder)
			{
				(alreadyAtDestination, path) = EvalPath(check);
				if (alreadyAtDestination || path.Count > 0)
					return;
			}
		}

		public override bool Tick(Actor self)
		{
			mobile.TurnToMove = false;

			if (IsCanceling && mobile.CanStayInCell(mobile.ToCell))
			{
				path?.Clear();

				mobile.MoveResult = MoveResult.CompleteCanceled;
				return true;
			}

			if (mobile.IsTraitDisabled || mobile.IsTraitPaused)
				return false;

			if (alreadyAtDestination)
			{
				mobile.MoveResult = MoveResult.CompleteDestinationReached;
				return true;
			}

			if (destination == mobile.ToCell)
			{
				if (hadNoPath)
					mobile.MoveResult = MoveResult.CompleteDestinationBlocked;
				else
					mobile.MoveResult = MoveResult.CompleteDestinationReached;

				return true;
			}

			if (path.Count == 0)
			{
				hadNoPath = true;
				destination = mobile.ToCell;
				return false;
			}

			destination = path[0];

			var (nextCell, shouldTryAgain) = PopPath(self);
			if (nextCell == null)
			{
				if (!shouldTryAgain)
				{
					mobile.MoveResult = MoveResult.CompleteDestinationBlocked;
					return true;
				}

				return false;
			}

			var firstFacing = self.World.Map.FacingBetween(mobile.FromCell, nextCell.Value.Cell, mobile.Facing);

			if (mobile.Info.CanMoveBackward
				&& (mobile.Info.MaxBackwardCells < 0 || path.Count < mobile.Info.MaxBackwardCells)
				&& (mobile.Info.BackwardDuration < 0 || self.World.WorldTick - startTicks < mobile.Info.BackwardDuration)
				&& Math.Abs(firstFacing.Angle - mobile.Facing.Angle) > 256)
			{
				ActorFacingModifier = new WAngle(512);
				firstFacing += ActorFacingModifier;
			}
			else
				ActorFacingModifier = WAngle.Zero;

			if (!mobile.Info.TurnsWhileMoving && firstFacing != mobile.Facing)
			{
				path.Add(nextCell.Value.Cell);
				QueueChild(new Turn(self, firstFacing));
				mobile.TurnToMove = true;
				return false;
			}

			mobile.SetLocation(mobile.FromCell, mobile.FromSubCell, nextCell.Value.Cell, nextCell.Value.SubCell);

			var map = self.World.Map;
			var from = (mobile.FromCell.Layer == 0 ? map.CenterOfCell(mobile.FromCell) :
				self.World.GetCustomMovementLayers()[mobile.FromCell.Layer].CenterOfCell(mobile.FromCell)) +
				map.Grid.OffsetOfSubCell(mobile.FromSubCell);

			var to = Util.BetweenCells(self.World, mobile.FromCell, mobile.ToCell) +
				(map.Grid.OffsetOfSubCell(mobile.FromSubCell) + map.Grid.OffsetOfSubCell(mobile.ToSubCell)) / 2;

			WRot? toTerrainOrientation = null;
			var margin = mobile.Info.TerrainOrientationAdjustmentMargin.Length;
			if (margin >= 0)
				toTerrainOrientation = WRot.SLerp(map.TerrainOrientation(mobile.FromCell), map.TerrainOrientation(mobile.ToCell), 1, 2);

			var movingOnGroundLayer = mobile.FromCell.Layer == 0 && mobile.ToCell.Layer == 0;
			QueueChild(new MoveFirstHalf(this, from, to, mobile.Facing, firstFacing, null, toTerrainOrientation, margin, carryoverProgress, false, movingOnGroundLayer));
			carryoverProgress = 0;
			return false;
		}

		((CPos Cell, SubCell SubCell)? Next, bool ShouldTryAgain) PopPath(Actor self)
		{
			if (path.Count == 0)
				return (null, false);

			var nextCell = path[^1];

			// Something else might have moved us, so the path is no longer valid.
			if (!Util.AreAdjacentCells(mobile.ToCell, nextCell))
			{
				(alreadyAtDestination, path) = EvalPath(BlockedByActor.Immovable);
				return (null, false);
			}

			var containsTemporaryBlocker = self.World.ContainsTemporaryBlocker(nextCell, self);

			// Next cell in the move is blocked by another actor
			if (containsTemporaryBlocker || !mobile.CanEnterCell(nextCell, ignoreActor))
			{
				// Are we close enough?
				var cellRange = nearEnough.Length / 1024;
				if (!containsTemporaryBlocker && (mobile.ToCell - destination.Value).LengthSquared <= cellRange * cellRange && mobile.CanStayInCell(mobile.ToCell))
				{
					// Apply some simple checks to avoid giving up in cases where we can be confident that
					// nudging/waiting/repathing should produce better results.

					// Avoid fighting over the destination cell
					if (path.Count < 2)
					{
						path.Clear();
						return (null, false);
					}

					// We can reasonably assume that the blocker is friendly and has a similar locomotor type.
					// If there is a free cell next to the blocker that is a similar or closer distance to the
					// destination then we can probably nudge or path around it.
					var blockerDistSq = (nextCell - destination.Value).LengthSquared;
					var nudgeOrRepath = CVec.Directions
						.Select(d => nextCell + d)
						.Any(c => c != self.Location && (c - destination.Value).LengthSquared <= blockerDistSq && mobile.CanEnterCell(c, ignoreActor));

					if (!nudgeOrRepath)
					{
						path.Clear();
						return (null, false);
					}
				}

				// There is no point in waiting for the other actor to move if it is incapable of moving.
				if (!mobile.CanEnterCell(nextCell, ignoreActor, BlockedByActor.Immovable))
				{
					(alreadyAtDestination, path) = EvalPath(BlockedByActor.Immovable);
					return (null, false);
				}

				// See if they will move
				self.NotifyBlocker(nextCell);

				// Wait a bit to see if they leave
				if (!hasWaited)
				{
					waitTicksRemaining = mobile.Info.LocomotorInfo.WaitAverage;
					hasWaited = true;
					return (null, true);
				}

				if (--waitTicksRemaining >= 0)
					return (null, true);

				hasWaited = false;

				// If the blocking actors are already leaving, wait a little longer instead of repathing
				if (CellIsEvacuating(self, nextCell))
					return (null, true);

				// Calculate a new path
				mobile.RemoveInfluence();
				var (_, newPath) = EvalPath(BlockedByActor.All);
				mobile.AddInfluence();

				if (newPath.Count != 0)
				{
					path = newPath;
					var newCell = path[^1];
					path.RemoveAt(path.Count - 1);

					return ((newCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor)), true);
				}
				else if (mobile.IsBlocking)
				{
					// If there is no way around the blocker and blocker will not move and we are blocking others, back up to let others pass.
					var newCell = mobile.GetAdjacentCell(nextCell);
					if (newCell != null)
					{
						if ((nextCell - newCell).Value.LengthSquared > 2)
							path.Add(mobile.ToCell);

						return ((newCell.Value, mobile.GetAvailableSubCell(newCell.Value, mobile.FromSubCell, ignoreActor)), true);
					}
				}

				return (null, false);
			}

			hasWaited = false;
			path.RemoveAt(path.Count - 1);

			return ((nextCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor)), true);
		}

		protected override void OnLastRun(Actor self)
		{
			path = null;
		}

		static bool CellIsEvacuating(Actor self, CPos cell)
		{
			foreach (var actor in self.World.ActorMap.GetActorsAt(cell))
			{
				if (actor.OccupiesSpace is not Mobile move || move.IsTraitDisabled || !move.IsLeaving())
					return false;
			}

			return true;
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			Cancel(self, keepQueue, false);
		}

		public void Cancel(Actor self, bool keepQueue, bool forceClearPath)
		{
			// We need to clear the path here in order to prevent MovePart queueing new instances of itself
			// when the unit is making a turn.
			if (path != null && (forceClearPath || mobile.CanStayInCell(mobile.ToCell)))
				path.Clear();

			base.Cancel(self, keepQueue);
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			if (path != null)
				return Enumerable.Reverse(path).Select(c => Target.FromCell(self.World, c));
			if (destination != null)
				return [Target.FromCell(self.World, destination.Value)];
			return Target.None;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			// destination might be initialized with null, but will be set in a subsequent tick
			if (targetLineColor != null && destination != null)
				yield return new TargetLineNode(Target.FromCell(self.World, destination.Value), targetLineColor.Value);
		}

		abstract class MovePart : Activity
		{
			protected readonly Move Move;
			protected readonly WPos From, To;
			protected readonly WAngle FromFacing, ToFacing;
			protected readonly WRot? FromTerrainOrientation, ToTerrainOrientation;
			protected readonly bool EnableArc;
			protected readonly WPos ArcCenter;
			protected readonly int ArcFromLength;
			protected readonly WAngle ArcFromAngle;
			protected readonly int ArcToLength;
			protected readonly WAngle ArcToAngle;
			protected readonly int Distance;
			protected readonly bool MovingOnGroundLayer;
			protected readonly bool TurnsWhileMoving;
			readonly int terrainOrientationMargin;
			protected int progress;

			protected MovePart(Move move, WPos from, WPos to, WAngle fromFacing, WAngle toFacing,
				WRot? fromTerrainOrientation, WRot? toTerrainOrientation, int terrainOrientationMargin,
				int carryoverProgress, bool shouldArc, bool movingOnGroundLayer)
			{
				ActivityType = ActivityType.Move;
				Move = move;
				From = from;
				To = to;
				FromFacing = fromFacing;
				ToFacing = toFacing;
				FromTerrainOrientation = fromTerrainOrientation;
				ToTerrainOrientation = toTerrainOrientation;
				progress = carryoverProgress;
				Distance = (to - from).Length;
				this.terrainOrientationMargin = Math.Min(terrainOrientationMargin, Distance / 2);
				MovingOnGroundLayer = movingOnGroundLayer;

				IsInterruptible = false; // See comments in Move.Cancel()

				TurnsWhileMoving = move.mobile.Info.TurnsWhileMoving;

				// Calculate an elliptical arc that joins from and to
				if (shouldArc)
				{
					// The center of rotation is where the normal vectors cross
					var u = new WVec(1024, 0, 0).Rotate(WRot.FromYaw(fromFacing));
					var v = new WVec(1024, 0, 0).Rotate(WRot.FromYaw(toFacing));

					// Make sure that u and v aren't parallel, which may happen due to rounding
					// in WVec.Rotate if delta is close but not necessarily equal to 0 or 512
					if (v.X * u.Y != v.Y * u.X)
					{
						var w = from - to;
						var s = (v.Y * w.X - v.X * w.Y) * 1024 / (v.X * u.Y - v.Y * u.X);
						var x = from.X + s * u.X / 1024;
						var y = from.Y + s * u.Y / 1024;

						ArcCenter = new WPos(x, y, 0);
						ArcFromLength = (ArcCenter - from).HorizontalLength;
						ArcFromAngle = (ArcCenter - from).Yaw;
						ArcToLength = (ArcCenter - to).HorizontalLength;
						ArcToAngle = (ArcCenter - to).Yaw;
						EnableArc = true;
					}
				}
			}

			public override bool Tick(Actor self)
			{
				var mobile = Move.mobile;

				// Only move by a full speed step if we didn't already move this tick.
				// If we did, we limit the move to any carried-over leftover progress.
				if (Move.lastMovePartCompletedTick < self.World.WorldTick)
					progress += mobile.MovementSpeedForCell(mobile.ToCell);

				if (progress >= Distance)
				{
					var toPos = To;

					// apply ramp offset to ground units
					if (MovingOnGroundLayer)
						toPos -= new WVec(WDist.Zero, WDist.Zero, self.World.Map.DistanceAboveTerrain(toPos));
					mobile.SetCenterPosition(self, toPos);

					mobile.Facing = TurnsWhileMoving
						? Util.TickFacing(mobile.Facing, ToFacing, mobile.TurnSpeed)
						: ToFacing;

					Move.lastMovePartCompletedTick = self.World.WorldTick;
					Queue(OnComplete(self, mobile, Move));
					return true;
				}

				WPos pos;
				if (EnableArc)
				{
					var angle = WAngle.Lerp(ArcFromAngle, ArcToAngle, progress, Distance);
					var length = int2.Lerp(ArcFromLength, ArcToLength, progress, Distance);
					var height = int2.Lerp(From.Z, To.Z, progress, Distance);
					pos = ArcCenter + new WVec(0, length, height).Rotate(WRot.FromYaw(angle));
				}
				else
					pos = WPos.Lerp(From, To, progress, Distance);

				// This makes sure units move smoothly moves over ramps
				// HACK: DistanceAboveTerrain works only with ground layer
				if (MovingOnGroundLayer)
					pos -= new WVec(WDist.Zero, WDist.Zero, self.World.Map.DistanceAboveTerrain(pos));

				mobile.SetCenterPosition(self, pos);

				// Smoothly interpolate over terrain orientation changes
				if (FromTerrainOrientation.HasValue && progress < terrainOrientationMargin)
				{
					var currentCellOrientation = self.World.Map.TerrainOrientation(mobile.FromCell);
					var orientation = WRot.SLerp(FromTerrainOrientation.Value, currentCellOrientation, progress, terrainOrientationMargin);
					mobile.SetTerrainRampOrientation(orientation);
				}
				else if (ToTerrainOrientation.HasValue && Distance - progress < terrainOrientationMargin)
				{
					var currentCellOrientation = self.World.Map.TerrainOrientation(mobile.FromCell);
					var orientation = WRot.SLerp(ToTerrainOrientation.Value, currentCellOrientation, Distance - progress, terrainOrientationMargin);
					mobile.SetTerrainRampOrientation(orientation);
				}

				mobile.Facing = TurnsWhileMoving
					? Util.TickFacing(mobile.Facing, ToFacing, mobile.TurnSpeed)
					: WAngle.Lerp(FromFacing, ToFacing, progress, Distance);

				return false;
			}

			protected abstract MovePart OnComplete(Actor self, Mobile mobile, Move parent);

			public override IEnumerable<Target> GetTargets(Actor self)
			{
				return Move.GetTargets(self);
			}
		}

		sealed class MoveFirstHalf : MovePart
		{
			public MoveFirstHalf(
				Move move, WPos from, WPos to, WAngle fromFacing, WAngle toFacing,
				WRot? fromTerrainOrientation, WRot? toTerrainOrientation, int terrainOrientationMargin, int carryoverProgress, bool shouldArc, bool movingOnGroundLayer)
				: base(
					  move, from, to, fromFacing, toFacing,
					  fromTerrainOrientation, toTerrainOrientation, terrainOrientationMargin, carryoverProgress, shouldArc, movingOnGroundLayer)
			{ }

			bool IsTurn(Actor self, Mobile mobile, CPos nextCell, Map map)
			{
				// Some actors with a limited number of sprite facings should never move along curved trajectories.
				if (mobile.Info.AlwaysTurnInPlace || TurnsWhileMoving)
					return false;

				// When Backwards duration runs out, let the Move activity do the turn.
				if (Move.ActorFacingModifier != WAngle.Zero && self.World.WorldTick - Move.startTicks >= mobile.Info.BackwardDuration)
					return false;

				// Tight U-turns should be done in place instead of making silly looking loops.
				var nextFacing = map.FacingBetween(nextCell, mobile.ToCell, mobile.Facing);
				var currentFacing = map.FacingBetween(mobile.ToCell, mobile.FromCell, mobile.Facing);
				var delta = (nextFacing - currentFacing).Angle;
				return delta != 0 && (delta < 384 || delta > 640);
			}

			protected override MovePart OnComplete(Actor self, Mobile mobile, Move parent)
			{
				var map = self.World.Map;
				var fromSubcellOffset = map.Grid.OffsetOfSubCell(mobile.FromSubCell);
				var toSubcellOffset = map.Grid.OffsetOfSubCell(mobile.ToSubCell);

				var (nextCell, _) = parent.PopPath(self);
				if (nextCell != null)
				{
					if (!mobile.IsTraitPaused && !mobile.IsTraitDisabled && IsTurn(self, mobile, nextCell.Value.Cell, map))
					{
						var nextSubcellOffset = map.Grid.OffsetOfSubCell(nextCell.Value.SubCell);
						WRot? nextToTerrainOrientation = null;
						var margin = mobile.Info.TerrainOrientationAdjustmentMargin.Length;
						if (margin >= 0)
							nextToTerrainOrientation = WRot.SLerp(map.TerrainOrientation(mobile.ToCell), map.TerrainOrientation(nextCell.Value.Cell), 1, 2);

						var ret = new MoveFirstHalf(
							Move,
							Util.BetweenCells(self.World, mobile.FromCell, mobile.ToCell) + (fromSubcellOffset + toSubcellOffset) / 2,
							Util.BetweenCells(self.World, mobile.ToCell, nextCell.Value.Cell) + (toSubcellOffset + nextSubcellOffset) / 2,
							mobile.Facing,
							map.FacingBetween(mobile.ToCell, nextCell.Value.Cell, mobile.Facing) + Move.ActorFacingModifier,
							ToTerrainOrientation,
							nextToTerrainOrientation,
							margin,
							progress - Distance,
							true,
							mobile.ToCell.Layer == 0 && nextCell.Value.Cell.Layer == 0);

						mobile.FinishedMoving(self);
						mobile.SetLocation(mobile.ToCell, mobile.ToSubCell, nextCell.Value.Cell, nextCell.Value.SubCell);
						return ret;
					}

					parent.path.Add(nextCell.Value.Cell);
				}

				var toPos = mobile.ToCell.Layer == 0 ? map.CenterOfCell(mobile.ToCell) :
					self.World.GetCustomMovementLayers()[mobile.ToCell.Layer].CenterOfCell(mobile.ToCell);

				var ret2 = new MoveSecondHalf(
					Move,
					Util.BetweenCells(self.World, mobile.FromCell, mobile.ToCell) + (fromSubcellOffset + toSubcellOffset) / 2,
					toPos + toSubcellOffset,
					mobile.Facing,
					TurnsWhileMoving ? map.FacingBetween(mobile.FromCell, mobile.ToCell, mobile.Facing) + Move.ActorFacingModifier : mobile.Facing,
					ToTerrainOrientation,
					null,
					mobile.Info.TerrainOrientationAdjustmentMargin.Length,
					progress - Distance,
					false,
					MovingOnGroundLayer);

				mobile.EnteringCell(self);
				mobile.SetLocation(mobile.ToCell, mobile.ToSubCell, mobile.ToCell, mobile.ToSubCell);
				return ret2;
			}
		}

		sealed class MoveSecondHalf : MovePart
		{
			public MoveSecondHalf(
				Move move, WPos from, WPos to, WAngle fromFacing, WAngle toFacing,
				WRot? fromTerrainOrientation, WRot? toTerrainOrientation, int terrainOrientationMargin, int carryoverProgress, bool shouldArc, bool movingOnGroundLayer)
				: base(
					  move, from, to, fromFacing, toFacing,
					  fromTerrainOrientation, toTerrainOrientation, terrainOrientationMargin, carryoverProgress, shouldArc, movingOnGroundLayer)
			{ }

			protected override MovePart OnComplete(Actor self, Mobile mobile, Move parent)
			{
				mobile.SetPosition(self, mobile.ToCell);

				// Move might immediately queue a new MoveFirstHalf within the same tick if we haven't
				// reached the end of the requested path. Make sure that any leftover movement progress is
				// correctly carried over into this new activity to avoid a glitch in the apparent move speed.
				Move.carryoverProgress = progress - Distance;
				return null;
			}
		}
	}
}
