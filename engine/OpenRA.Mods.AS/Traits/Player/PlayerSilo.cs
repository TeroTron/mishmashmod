#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	[Desc("Used for silos defined on the player actor.")]
	class PlayerSiloInfo : TraitInfo, IStoresResourcesInfo
	{
		[FieldLoader.Require]
		[Desc("The amounts of resources that can be stored.")]
		public readonly int Capacity = 28;

		[Desc("Which resources can be stored.")]
		public readonly string[] Resources = Array.Empty<string>();

		string[] IStoresResourcesInfo.ResourceTypes => Resources;

		public override object Create(ActorInitializer init) { return new PlayerSilo(this); }
	}

	class PlayerSilo : IStoresResources, ISync
	{
		readonly PlayerSiloInfo info;

		readonly Dictionary<string, int> contents = new();

		[Sync]
		public int ContentHash
		{
			get
			{
				var value = 0;
				foreach (var c in contents)
					value += c.Value << c.Key.Length;

				return value;
			}
		}

		public int ContentsSum { get; private set; } = 0;
		public IReadOnlyDictionary<string, int> Contents { get; }
		int IStoresResources.Capacity => info.Capacity;

		public PlayerSilo(PlayerSiloInfo info)
		{
			this.info = info;

			foreach (var r in info.Resources)
				contents[r] = 0;

			Contents = new ReadOnlyDictionary<string, int>(contents);
		}

		public bool HasType(string resourceType)
		{
			return info.Resources.Contains(resourceType);
		}

		int IStoresResources.AddResource(string resourceType, int value)
		{
			if (!HasType(resourceType))
				return value;

			if (ContentsSum + value > info.Capacity)
			{
				var added = info.Capacity - ContentsSum;
				contents[resourceType] += added;
				ContentsSum = info.Capacity;
				return value - added;
			}

			contents[resourceType] += value;
			ContentsSum += value;
			return 0;
		}

		int IStoresResources.RemoveResource(string resourceType, int value)
		{
			if (!HasType(resourceType))
				return value;

			if (contents[resourceType] < value)
			{
				var leftover = value - contents[resourceType];
				ContentsSum -= contents[resourceType];
				contents[resourceType] = 0;
				return leftover;
			}

			contents[resourceType] -= value;
			ContentsSum -= value;
			return 0;
		}
	}
}
