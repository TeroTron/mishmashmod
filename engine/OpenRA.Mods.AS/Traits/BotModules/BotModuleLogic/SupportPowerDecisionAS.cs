﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Adds metadata for the AI bots.")]
	public class SupportPowerDecisionAS
	{
		[Desc("What is the minimum attractiveness we will use this power for?")]
		public readonly int MinimumAttractiveness = 1;

		[Desc("What support power does this decision apply to?")]
		public readonly string OrderName = "AirstrikePowerInfoOrder";

		[FieldLoader.LoadUsing(nameof(LoadConsiderations))]
		[Desc("The decisions associated with this power")]
		public readonly List<Consideration> Considerations = new();

		[Desc("Against whom should this power be used?", "Allowed keywords: Ally, Neutral, Enemy")]
		public readonly PlayerRelationship Against = PlayerRelationship.Enemy;

		[Desc("What types should the desired targets of this power be?")]
		public readonly BitSet<TargetableType> Types = new("Air", "Ground", "Water");

		[Desc("Should the AI ignore visibility rules during activation?")]
		public readonly bool IgnoreVisibility = false;

		[Desc("Minimum ticks to wait until next Decision scan attempt.")]
		public readonly int MinimumScanTimeInterval = 250;

		[Desc("Maximum ticks to wait until next Decision scan attempt.")]
		public readonly int MaximumScanTimeInterval = 262;

		public SupportPowerDecisionAS(MiniYaml yaml)
		{
			FieldLoader.Load(this, yaml);
		}

		static object LoadConsiderations(MiniYaml yaml)
		{
			var ret = new List<Consideration>();
			foreach (var d in yaml.Nodes)
				if (d.Key.Split('@')[0] == "Consideration")
					ret.Add(new Consideration(d.Value));

			return ret;
		}

		/// <summary>Evaluates the attractiveness of a position according to all considerations.</summary>
		public int GetAttractiveness(WPos pos, Player firedBy)
		{
			var answer = 0;
			var world = firedBy.World;
			var targetTile = world.Map.CellContaining(pos);

			if (!world.Map.Contains(targetTile))
				return 0;

			foreach (var consideration in Considerations)
			{
				var radiusToUse = new WDist(consideration.CheckRadius.Length);

				var checkActors = world.FindActorsInCircle(pos, radiusToUse);
				foreach (var scrutinized in checkActors)
					answer += consideration.GetAttractiveness(scrutinized, firedBy.RelationshipWith(scrutinized.Owner), firedBy, IgnoreVisibility);

				var delta = new WVec(radiusToUse, radiusToUse, WDist.Zero);
				var tl = world.Map.CellContaining(pos - delta);
				var br = world.Map.CellContaining(pos + delta);
				var checkFrozen = firedBy.FrozenActorLayer.FrozenActorsInRegion(new CellRegion(world.Map.Grid.Type, tl, br));

				// IsValid check filters out Frozen Actors that have not initizialized their Owner
				foreach (var scrutinized in checkFrozen)
					answer += consideration.GetAttractiveness(scrutinized, firedBy.RelationshipWith(scrutinized.Owner), firedBy);
			}

			return answer;
		}

		public int GetAttractiveness(IEnumerable<FrozenActor> frozenActors, Player firedBy)
		{
			var answer = 0;

			foreach (var consideration in Considerations)
				foreach (var scrutinized in frozenActors)
					if (scrutinized.IsValid && scrutinized.Visible)
						answer += consideration.GetAttractiveness(scrutinized, firedBy.RelationshipWith(scrutinized.Owner), firedBy);

			return answer;
		}

		public int GetNextScanTime(World world) { return world.LocalRandom.Next(MinimumScanTimeInterval, MaximumScanTimeInterval); }

		/// <summary>Makes up part of a decision, describing how to evaluate a target.</summary>
		public class Consideration
		{
			public enum DecisionMetric { Health, Value, None }

			[Desc("Against whom should this power be used?", "Allowed keywords: Ally, Neutral, Enemy")]
			public readonly PlayerRelationship Against = PlayerRelationship.Enemy;

			[Desc("What types should the desired targets of this power be?")]
			public readonly BitSet<TargetableType> Types = new("Air", "Ground", "Water");

			[Desc("How attractive are these types of targets?")]
			public readonly int Attractiveness = 100;

			[Desc("Weight the target attractiveness by this property", "Allowed keywords: Health, Value, None")]
			public readonly DecisionMetric TargetMetric = DecisionMetric.None;

			[Desc("What is the check radius of this decision?")]
			public readonly WDist CheckRadius = WDist.FromCells(5);

			public Consideration(MiniYaml yaml)
			{
				FieldLoader.Load(this, yaml);
			}

			/// <summary>Evaluates a single actor according to the rules defined in this consideration.</summary>
			public int GetAttractiveness(Actor a, PlayerRelationship relationship, Player firedBy, bool ignoreVisibility)
			{
				if (relationship != Against)
					return 0;

				if (a == null)
					return 0;

				if (!ignoreVisibility && (!a.IsTargetableBy(firedBy.PlayerActor) || !a.CanBeViewedByPlayer(firedBy)))
					return 0;

				if (Types.Overlaps(a.GetEnabledTargetTypes()))
				{
					switch (TargetMetric)
					{
						case DecisionMetric.Value:
							var valueInfo = a.Info.TraitInfoOrDefault<ValuedInfo>();
							return (valueInfo != null) ? valueInfo.Cost * Attractiveness : 0;

						case DecisionMetric.Health:
							var health = a.TraitOrDefault<IHealth>();

							if (health == null)
								return 0;

							// Cast to long to avoid overflow when multiplying by the health
							return (int)((long)health.HP * Attractiveness / health.MaxHP);

						default:
							return Attractiveness;
					}
				}

				return 0;
			}

			public int GetAttractiveness(FrozenActor fa, PlayerRelationship relationship, Player firedBy)
			{
				if (relationship != Against)
					return 0;

				if (fa == null || !fa.IsValid || !fa.Visible)
					return 0;

				if (Types.Overlaps(fa.TargetTypes))
				{
					switch (TargetMetric)
					{
						case DecisionMetric.Value:
							var valueInfo = fa.Info.TraitInfoOrDefault<ValuedInfo>();
							return (valueInfo != null) ? valueInfo.Cost * Attractiveness : 0;

						case DecisionMetric.Health:
							var healthInfo = fa.Info.TraitInfoOrDefault<IHealthInfo>();
							return (healthInfo != null) ? fa.HP * Attractiveness / healthInfo.MaxHP : 0;

						default:
							return Attractiveness;
					}
				}

				return 0;
			}
		}
	}
}
