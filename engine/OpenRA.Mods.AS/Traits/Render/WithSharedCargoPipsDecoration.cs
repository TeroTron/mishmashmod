#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits.Render
{
	public class WithSharedCargoPipsDecorationInfo : WithDecorationBaseInfo, Requires<SharedCargoInfo>
	{
		[Desc("Number of pips to display. Defaults to Cargo.MaxWeight.")]
		public readonly int PipCount = -1;

		[Desc("If non-zero, override the spacing between adjacent pips.")]
		public readonly int2 PipStride = int2.Zero;

		[Desc("Image that defines the pip sequences.")]
		public readonly string Image = "pips";

		[SequenceReference(nameof(Image))]
		[Desc("Sequence used for empty pips.")]
		public readonly string EmptySequence = "pip-empty";

		[SequenceReference(nameof(Image))]
		[Desc("Sequence used for full pips that aren't defined in CustomPipSequences.")]
		public readonly string FullSequence = "pip-green";

		// TODO: [SequenceReference] isn't smart enough to use Dictionaries.
		[Desc("Pip sequence to use for specific passenger actors.")]
		public readonly Dictionary<string, string> CustomPipSequences = new();

		[PaletteReference]
		public readonly string Palette = "chrome";

		public override object Create(ActorInitializer init) { return new WithSharedCargoPipsDecoration(init.Self, this); }
	}

	public class WithSharedCargoPipsDecoration : WithDecorationBase<WithSharedCargoPipsDecorationInfo>
	{
		readonly SharedCargo cargo;
		readonly Animation pips;
		readonly int pipCount;

		public WithSharedCargoPipsDecoration(Actor self, WithSharedCargoPipsDecorationInfo info)
			: base(self, info)
		{
			cargo = self.Trait<SharedCargo>();
			pipCount = info.PipCount > 0 ? info.PipCount : cargo.Manager.Info.MaxWeight;
			pips = new Animation(self.World, info.Image);
		}

		string GetPipSequence(int i)
		{
			var n = i * cargo.Manager.Info.MaxWeight / pipCount;

			foreach (var c in cargo.Manager.Passengers)
			{
				var pi = c.Info.TraitInfo<SharedPassengerInfo>();
				if (n < pi.Weight)
				{
					if (pi.CustomPipType != null && Info.CustomPipSequences.TryGetValue(pi.CustomPipType, out var sequence))
						return sequence;

					return Info.FullSequence;
				}

				n -= pi.Weight;
			}

			return Info.EmptySequence;
		}

		protected override IEnumerable<IRenderable> RenderDecoration(Actor self, WorldRenderer wr, int2 screenPos)
		{
			pips.PlayRepeating(Info.EmptySequence);

			var palette = wr.Palette(Info.Palette);
			var pipSize = pips.Image.Size.XY.ToInt2();
			var pipStride = Info.PipStride != int2.Zero ? Info.PipStride : new int2(pipSize.X, 0);

			screenPos -= pipSize / 2;
			for (var i = 0; i < pipCount; i++)
			{
				pips.PlayRepeating(GetPipSequence(i));
				yield return new UISpriteRenderable(pips.Image, self.CenterPosition, screenPos, 0, palette, 1f);

				screenPos += pipStride;
			}
		}
	}
}
