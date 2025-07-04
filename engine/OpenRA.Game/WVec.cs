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
using Eluant;
using Eluant.ObjectBinding;
using OpenRA.Scripting;
using OpenRA.Support;

namespace OpenRA
{
	public readonly struct WVec : IEquatable<WVec>, IScriptBindable,
		ILuaAdditionBinding, ILuaSubtractionBinding, ILuaEqualityBinding, ILuaUnaryMinusBinding,
		ILuaMultiplicationBinding, ILuaDivisionBinding, ILuaTableBinding, ILuaToStringBinding
	{
		public readonly int X, Y, Z;

		public WVec(int x, int y, int z) { X = x; Y = y; Z = z; }
		public WVec(WDist x, WDist y, WDist z) { X = x.Length; Y = y.Length; Z = z.Length; }

		public static readonly WVec Zero = new(0, 0, 0);

		public static WVec operator +(in WVec a, in WVec b) { return new WVec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
		public static WVec operator -(in WVec a, in WVec b) { return new WVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
		public static WVec operator -(in WVec a) { return new WVec(-a.X, -a.Y, -a.Z); }
		public static WVec operator /(in WVec a, int b) { return new WVec(a.X / b, a.Y / b, a.Z / b); }
		public static WVec operator *(int a, in WVec b) { return new WVec(a * b.X, a * b.Y, a * b.Z); }
		public static WVec operator *(in WVec a, int b) { return b * a; }

		public static bool operator ==(in WVec me, in WVec other) { return me.X == other.X && me.Y == other.Y && me.Z == other.Z; }
		public static bool operator !=(in WVec me, in WVec other) { return !(me == other); }

		public static int Dot(in WVec a, in WVec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
		public long LengthSquared => (long)X * X + (long)Y * Y + (long)Z * Z;
		public int Length => (int)Exts.ISqrt(LengthSquared);
		public long HorizontalLengthSquared => (long)X * X + (long)Y * Y;
		public int HorizontalLength => (int)Exts.ISqrt(HorizontalLengthSquared);
		public long VerticalLengthSquared => (long)Z * Z;
		public int VerticalLength => (int)Exts.ISqrt(VerticalLengthSquared);

		public WVec Rotate(in WRot rot)
		{
			rot.AsMatrix(out var mtx);
			return Rotate(ref mtx);
		}

		public WVec Rotate(ref Int32Matrix4x4 mtx)
		{
			var lx = (long)X;
			var ly = (long)Y;
			var lz = (long)Z;
			return new WVec(
				(int)((lx * mtx.M11 + ly * mtx.M21 + lz * mtx.M31) / mtx.M44),
				(int)((lx * mtx.M12 + ly * mtx.M22 + lz * mtx.M32) / mtx.M44),
				(int)((lx * mtx.M13 + ly * mtx.M23 + lz * mtx.M33) / mtx.M44));
		}

		public WAngle Yaw
		{
			get
			{
				if (LengthSquared == 0)
					return WAngle.Zero;

				// OpenRA defines north as -y
				return WAngle.ArcTan(-Y, X) - new WAngle(256);
			}
		}

		public WAngle Pitch
		{
			get
			{
				if (LengthSquared == 0)
					return WAngle.Zero;

				// OpenRA defines north as -y
				return WAngle.ArcTan(Z, HorizontalLength);
			}
		}

		public static WVec Lerp(in WVec a, in WVec b, int mul, int div) { return a + (b - a) * mul / div; }

		public static WVec LerpQuadratic(in WVec a, in WVec b, WAngle pitch, int mul, int div)
		{
			// Start with a linear lerp between the points
			var ret = Lerp(a, b, mul, div);

			if (pitch.Angle == 0)
				return ret;

			// Add an additional quadratic variation to height
			// Uses decimal to avoid integer overflow
			var offset = (int)((decimal)(b - a).Length * pitch.Tan() * mul * (div - mul) / (1024 * div * div));
			return new WVec(ret.X, ret.Y, ret.Z + offset);
		}

		// Sampled a N-sample probability density function in the range [-1024..1024, -1024..1024]
		// 1 sample produces a rectangular probability
		// 2 samples produces a triangular probability
		// ...
		// N samples approximates a true Gaussian
		public static WVec FromPDF(MersenneTwister r, int samples)
		{
			return new WVec(WDist.FromPDF(r, samples), WDist.FromPDF(r, samples), WDist.Zero);
		}

		public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode(); }

		public bool Equals(WVec other) { return other == this; }
		public override bool Equals(object obj) { return obj is WVec vec && Equals(vec); }

		public override string ToString() { return X + "," + Y + "," + Z; }

		#region Scripting interface

		public LuaValue Add(LuaRuntime runtime, LuaValue left, LuaValue right)
		{
			if (!left.TryGetClrValue(out WVec a) || !right.TryGetClrValue(out WVec b))
				throw new LuaException("Attempted to call WVec.Add(WVec, WVec) with invalid arguments " +
					$"({left.WrappedClrType().Name}, {right.WrappedClrType().Name})");

			return new LuaCustomClrObject(a + b);
		}

		public LuaValue Subtract(LuaRuntime runtime, LuaValue left, LuaValue right)
		{
			if (!left.TryGetClrValue(out WVec a) || !right.TryGetClrValue(out WVec b))
				throw new LuaException("Attempted to call WVec.Subtract(WVec, WVec) with invalid arguments " +
					$"({left.WrappedClrType().Name}, {right.WrappedClrType().Name})");

			return new LuaCustomClrObject(a - b);
		}

		public LuaValue Equals(LuaRuntime runtime, LuaValue left, LuaValue right)
		{
			if (!left.TryGetClrValue(out WVec a) || !right.TryGetClrValue(out WVec b))
				return false;

			return a == b;
		}

		public LuaValue Minus(LuaRuntime runtime)
		{
			return new LuaCustomClrObject(-this);
		}

		public LuaValue Multiply(LuaRuntime runtime, LuaValue left, LuaValue right)
		{
			if (!left.TryGetClrValue(out WVec a) || !right.TryGetClrValue(out int b))
				throw new LuaException("Attempted to call WVec.Multiply(WVec, integer) with invalid arguments " +
					$"({left.WrappedClrType().Name}, {right.WrappedClrType().Name})");

			return new LuaCustomClrObject(a * b);
		}

		public LuaValue Divide(LuaRuntime runtime, LuaValue left, LuaValue right)
		{
			if (!left.TryGetClrValue(out WVec a) || !right.TryGetClrValue(out int b))
				throw new LuaException("Attempted to call WVec.Divide(WVec, integer) with invalid arguments " +
					$"({left.WrappedClrType().Name}, {right.WrappedClrType().Name})");

			return new LuaCustomClrObject(a / b);
		}

		public LuaValue this[LuaRuntime runtime, LuaValue key]
		{
			get
			{
				switch (key.ToString())
				{
					case "X": return X;
					case "Y": return Y;
					case "Z": return Z;
					case "Facing": return new LuaCustomClrObject(Yaw);
					default: throw new LuaException($"WVec does not define a member '{key}'");
				}
			}

			set => throw new LuaException("WVec is read-only. Use WVec.New to create a new value");
		}

		public LuaValue ToString(LuaRuntime runtime) => ToString();

		#endregion
	}
}
