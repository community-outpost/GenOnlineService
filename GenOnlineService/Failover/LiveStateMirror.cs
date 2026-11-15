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

		// an entry of an older owner epoch, a fenced copy that wrote late
		Stale,

		// a missing sequence number or a new owner without its reset entry, the mirror needs a full reload
		Gap
	}

	// The standby's copy of the stored envelopes from a snapshot and the stream in order, no lobby logic, the follower serializes access
	public sealed class LiveStateMirror
	{
		private readonly Dictionary<string, string> m_entities = new(StringComparer.Ordinal);

		public bool Loaded { get; private set; }

		// The id of the last applied stream entry, 0-0 before any
		public string Position { get; private set; } = "0-0";

		// The writer whose entries are applied: its copy id, epoch and last sequence number
		public string? WriterCopy { get; private set; }
		public long WriterEpoch { get; private set; }
		public long WriterSeq { get; private set; }

		public int Count => m_entities.Count;

		public IReadOnlyDictionary<string, string> Entities => m_entities;

		// Replaces the mirror with a snapshot: entities of an older epoch than the owner's are a fenced copy's late writes and stay out
		public void Load(LiveSnapshot snapshot)
		{
			m_entities.Clear();
			foreach ((string key, string json) in snapshot.Entities)
			{
				LiveEnvelope? envelope = LiveJson.ReadEnvelope(json);
				if (envelope != null && envelope.Epoch < snapshot.OwnerEpoch)
				{
					continue;
				}
				m_entities[key] = json;
			}

			if (snapshot.Last != null)
			{
				Position = snapshot.Last.ID;
				WriterCopy = snapshot.Last.Copy;
				WriterEpoch = snapshot.Last.Epoch;
				WriterSeq = snapshot.Last.Seq;
			}
			else
			{
				Position = "0-0";
				WriterCopy = snapshot.OwnerCopy;
				WriterEpoch = snapshot.OwnerEpoch;
				WriterSeq = 0;
			}
			Loaded = true;
		}

		public void Unload()
		{
			m_entities.Clear();
			Loaded = false;
		}

		// Applies one entry in stream order. A gap changes nothing, the caller reloads.
		public MirrorApply Apply(LiveStreamEntry entry)
		{
			if (entry.Epoch < WriterEpoch)
			{
				Position = entry.ID;
				return MirrorApply.Stale;
			}

			bool sameWriter = entry.Epoch == WriterEpoch && String.Equals(entry.Copy, WriterCopy, StringComparison.Ordinal);
			if (!sameWriter)
			{
				// a new owner always starts with its reset entry, sequence 1
				if (entry.Op != LiveOp.Reset || entry.Seq != 1)
				{
					return MirrorApply.Gap;
				}
				WriterCopy = entry.Copy;
				WriterEpoch = entry.Epoch;
				WriterSeq = 0;
			}

			if (entry.Seq != WriterSeq + 1)
			{
				return MirrorApply.Gap;
			}

			switch (entry.Op)
			{
				case LiveOp.Reset:
					m_entities.Clear();
					break;
				case LiveOp.Delete:
					m_entities.Remove(entry.Key);
					break;
				case LiveOp.Set:
					if (entry.Body != null)
					{
						m_entities[entry.Key] = entry.Body;
					}
					break;
			}

			WriterSeq = entry.Seq;
			Position = entry.ID;
			return MirrorApply.Applied;
		}
	}
}
