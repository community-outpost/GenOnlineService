/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

namespace GenOnlineService.Failover
{
	public enum MirrorApply
	{
		Applied,

		// a missing sequence number of a writing copy, the stream was trimmed past the follower, which needs a full reload
		Gap
	}

	// The standby's copy of the stored envelopes from a snapshot and the stream in order, no lobby logic, the follower serializes access.
	// Redis refuses stale writes, so every entry is applied as it comes and only the write count of each copy is checked.
	public sealed class LiveStateMirror
	{
		private readonly Dictionary<string, string> m_entities = new(StringComparer.Ordinal);
		private readonly Dictionary<string, long> m_seqs = new(StringComparer.Ordinal);

		public bool Loaded { get; private set; }

		// The id of the last applied stream entry, 0-0 before any
		public string Position { get; private set; } = "0-0";

		public int Count => m_entities.Count;

		public IReadOnlyDictionary<string, string> Entities => m_entities;

		public long SeqOf(string copy) => m_seqs.TryGetValue(copy, out long seq) ? seq : 0;

		public void Load(LiveSnapshot snapshot)
		{
			m_entities.Clear();
			foreach ((string key, string json) in snapshot.Entities)
			{
				m_entities[key] = json;
			}
			m_seqs.Clear();
			foreach ((string copy, long seq) in snapshot.Seqs)
			{
				m_seqs[copy] = seq;
			}
			Position = snapshot.Position;
			Loaded = true;
		}

		public void Unload()
		{
			m_entities.Clear();
			m_seqs.Clear();
			Loaded = false;
		}

		// Applies one entry in stream order. A gap changes nothing, the caller reloads.
		public MirrorApply Apply(LiveStreamEntry entry)
		{
			if (entry.Seq != SeqOf(entry.Copy) + 1)
			{
				return MirrorApply.Gap;
			}

			if (entry.Op == LiveOp.Delete)
			{
				m_entities.Remove(entry.Key);
			}
			else if (entry.Body != null)
			{
				m_entities[entry.Key] = entry.Body;
			}

			m_seqs[entry.Copy] = entry.Seq;
			Position = entry.ID;
			return MirrorApply.Applied;
		}
	}
}
