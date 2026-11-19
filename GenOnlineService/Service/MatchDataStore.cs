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

using Amazon.S3;
using Amazon.S3.Model;

namespace GenOnlineService.Service
{
	public sealed record StoredObject(byte[] Bytes, string ContentType);

	// The match data bucket (replays, screenshots and the evidence copies), as the service reads and writes it itself
	public interface IMatchDataStore
	{
		// Every key under the prefix
		Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default);

		// Null when no object has the key, for example a screenshot its TTL removed
		Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default);

		Task PutAsync(string key, StoredObject value, CancellationToken cancellationToken = default);

		// Deleting a key that does not exist succeeds
		Task DeleteAsync(string key, CancellationToken cancellationToken = default);
	}

	public sealed class S3MatchDataStore : IMatchDataStore
	{
		private readonly IAmazonS3 m_client;
		private readonly string m_bucket;

		public S3MatchDataStore(IAmazonS3 client, string bucket)
		{
			m_client = client;
			m_bucket = bucket;
		}

		public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
		{
			var keys = new List<string>();
			var request = new ListObjectsV2Request { BucketName = m_bucket, Prefix = prefix };
			ListObjectsV2Response response;
			do
			{
				response = await m_client.ListObjectsV2Async(request, cancellationToken);
				keys.AddRange((response.S3Objects ?? new List<S3Object>()).Select(o => o.Key));
				request.ContinuationToken = response.NextContinuationToken;
			}
			while (response.IsTruncated == true);
			return keys;
		}

		public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
		{
			try
			{
				using GetObjectResponse response = await m_client.GetObjectAsync(m_bucket, key, cancellationToken);
				using var buffer = new MemoryStream();
				await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
				return new StoredObject(buffer.ToArray(), response.Headers.ContentType ?? "application/octet-stream");
			}
			catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				return null;
			}
		}

		// A fresh PUT of the bytes, never a server side copy: SeaweedFS may carry the source's TTL over to a copy
		public async Task PutAsync(string key, StoredObject value, CancellationToken cancellationToken = default)
		{
			using var body = new MemoryStream(value.Bytes);
			await m_client.PutObjectAsync(new PutObjectRequest { BucketName = m_bucket, Key = key, InputStream = body, ContentType = value.ContentType }, cancellationToken);
		}

		public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
		{
			await m_client.DeleteObjectAsync(m_bucket, key, cancellationToken);
		}
	}
}
