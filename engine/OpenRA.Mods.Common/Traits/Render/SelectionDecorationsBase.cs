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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	public abstract class SelectionDecorationsBaseInfo : TraitInfo
	{
		public readonly Color SelectionBoxColor = Color.White;

		[Desc("Minimum zoom level to render the selection decorations.")]
		public readonly float MinimumZoom = 1f;
	}

	public abstract class SelectionDecorationsBase : ISelectionDecorations, IRenderAnnotations, INotifyCreated
	{
		IDecoration[] decorations;
		IDecoration[] selectedDecorations;

		protected readonly SelectionDecorationsBaseInfo Info;

		DeveloperMode developerMode;

		protected SelectionDecorationsBase(SelectionDecorationsBaseInfo info)
		{
			Info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			selectedDecorations = self.TraitsImplementing<IDecoration>().ToArray();
			decorations = selectedDecorations.Where(d => !d.RequiresSelection).ToArray();
		}

		static IEnumerable<WPos> ActivityTargetPath(Actor self)
		{
			if (!self.IsInWorld || self.IsDead)
				yield break;

			var activity = self.CurrentActivity;
			if (activity != null)
			{
				var targets = activity.GetTargets(self);
				yield return self.CenterPosition;

				foreach (var t in targets.Where(t => t.Type != TargetType.Invalid))
					yield return t.CenterPosition;
			}
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (self.World.FogObscures(self))
				return [];

			return DrawDecorations(self, wr);
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;

		IEnumerable<IRenderable> DrawDecorations(Actor self, WorldRenderer wr)
		{
			var selected = self.World.Selection.Contains(self);
			var regularWorld = self.World.Type == WorldType.Regular;
			var statusBars = Game.Settings.Game.StatusBars;

			// Health bars are shown when:
			//  * actor is selected / in active drag rectangle / under the mouse
			//  * status bar preference is set to "always show"
			//  * status bar preference is set to "when damaged" and actor is damaged
			var displayHealth = selected || (regularWorld && statusBars == StatusBarsType.AlwaysShow)
				|| (regularWorld && statusBars == StatusBarsType.DamageShow && self.GetDamageState() != DamageState.Undamaged);

			// Extra bars are shown when:
			//  * actor is selected / in active drag rectangle / under the mouse
			//  * status bar preference is set to "always show" or "when damaged"
			var displayExtra = selected || (regularWorld && statusBars != StatusBarsType.Standard);

			// PERF: Only search rollover enumerable if needed.
			if (!displayHealth || !displayExtra)
				if (self.World.Selection.RolloverContains(self))
					displayHealth = displayExtra = true;

			if (selected)
				foreach (var r in RenderSelectionBox(self, wr, Info.SelectionBoxColor))
					yield return r;

			if (displayHealth || displayExtra)
				foreach (var r in RenderSelectionBars(self, wr, displayHealth, displayExtra))
					yield return r;

			if (selected && self.World.LocalPlayer != null)
			{
				developerMode ??= self.World.LocalPlayer.PlayerActor.Trait<DeveloperMode>();

				if (developerMode.PathDebug)
					yield return new TargetLineRenderable(ActivityTargetPath(self), Color.Green, 1, 2);
			}

			// Hide decorations for spectators that zoom out further than the normal minimum level
			// This avoids graphical glitches with pip rows and icons overlapping the selection box
			if (wr.Viewport.Zoom < Info.MinimumZoom)
				yield break;

			var renderDecorations = selected ? selectedDecorations : decorations;
			foreach (var r in renderDecorations)
				foreach (var rr in r.RenderDecoration(self, wr, this))
					yield return rr;
		}

		IEnumerable<IRenderable> ISelectionDecorations.RenderSelectionAnnotations(Actor self, WorldRenderer worldRenderer, Color color)
		{
			return RenderSelectionBox(self, worldRenderer, color);
		}

		int2 ISelectionDecorations.GetDecorationOrigin(Actor self, WorldRenderer wr, string pos, int2 margin)
		{
			return GetDecorationOrigin(self, wr, pos, margin);
		}

		protected abstract int2 GetDecorationOrigin(Actor self, WorldRenderer wr, string pos, int2 margin);
		protected abstract IEnumerable<IRenderable> RenderSelectionBox(Actor self, WorldRenderer wr, Color color);
		protected abstract IEnumerable<IRenderable> RenderSelectionBars(Actor self, WorldRenderer wr, bool displayHealth, bool displayExtra);
	}
}
