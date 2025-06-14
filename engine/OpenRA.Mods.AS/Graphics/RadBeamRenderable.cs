﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.AS.Graphics
{
	public readonly struct RadBeamRenderable : IRenderable, IFinalizedRenderable
	{
		readonly WVec sourceToTarget;
		readonly WDist width;
		readonly Color color;
		readonly WDist amplitude;
		readonly WDist wavelength;
		readonly int quantizationCount;

		public RadBeamRenderable(
				WPos pos,
				int zOffset,
				WVec sourceToTarget,
				WDist width, Color color,
				WDist amplitude, WDist wavelength,
				int quantizationCount)
		{
			Pos = pos;
			ZOffset = zOffset;
			this.sourceToTarget = sourceToTarget;
			this.width = width;
			this.color = color;
			this.amplitude = amplitude;
			this.wavelength = wavelength;
			this.quantizationCount = quantizationCount;
		}

		public WPos Pos { get; }
		public PaletteReference Palette { get { return null; } }
		public int ZOffset { get; }
		public bool IsDecoration { get { return true; } }

		public IRenderable WithPalette(PaletteReference newPalette)
		{
			return new RadBeamRenderable(Pos, ZOffset, sourceToTarget, width, color, amplitude, wavelength, quantizationCount);
		}

		public IRenderable WithZOffset(int newOffset)
		{
			return new RadBeamRenderable(Pos, ZOffset, sourceToTarget, width, color, amplitude, wavelength, quantizationCount);
		}

		public IRenderable OffsetBy(in WVec vec)
		{
			return new RadBeamRenderable(Pos + vec, ZOffset, sourceToTarget, width, color, amplitude, wavelength, quantizationCount);
		}

		public IRenderable AsDecoration() { return this; }

		public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }
		public void Render(WorldRenderer wr)
		{
			if (sourceToTarget == WVec.Zero)
				return;

			// WAngle.Sin(x) = 1024 * Math.Sin(2pi/1024 * x)

			// forward step, pointing from src to target.
			// QuantizationCont * forwardStep == One cycle of beam in src2target direction.
			var forwardStep = wavelength.Length * sourceToTarget / (quantizationCount * sourceToTarget.Length);

			var cycleCnt = sourceToTarget.Length / wavelength.Length;
			if (sourceToTarget.Length % wavelength.Length != 0)
				cycleCnt++; // I'm emulating math.ceil

			var screenWidth = wr.ScreenVector(new WVec(width, WDist.Zero, WDist.Zero))[0];

			var angle = new WAngle(0);
			var angleStep = new WAngle(1024 / quantizationCount);

			// last point the rad beam "reached"
			var pos = Pos; // where we are.
			var last = wr.Screen3DPosition(pos); // we start from the shooter
			for (var i = 0; i < cycleCnt * quantizationCount; i++)
			{
				var y = new WVec(0, 0, amplitude.Length * angle.Sin() / 1024);
				var end = wr.Screen3DPosition(pos + y);
				Game.Renderer.WorldRgbaColorRenderer.DrawLine(last, end, screenWidth, color);

				pos += forwardStep; // keep moving along x axis
				last = end;
				angle += angleStep;
			}
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
	}
}
