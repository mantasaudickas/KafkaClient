using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using KafkaClient.Common;

namespace KafkaClient.Protocol
{
    /// <summary>
    /// DescribeGroupsRequest => [GroupId]
    ///   GroupId => string
    ///
    /// From http://kafka.apache.org/protocol.html#protocol_messages
    /// 
    /// This API can be used to find the current groups managed by a broker. To get a list of all groups in the cluster, 
    /// you must send ListGroup to all brokers.
    /// </summary>
    public class DescribeGroupsRequest : Request, IRequest<DescribeGroupsResponse>, IEquatable<DescribeGroupsRequest>
    {
        public override string ToString() => $"{{Api:{ApiKey},GroupIds:[{GroupIds.ToStrings()}]}}";

        public override string ShortString() => GroupIds.Count == 1 ? $"{ApiKey} {GroupIds[0]}" : ApiKey.ToString();

        public DescribeGroupsRequest(params string[] groupIds) 
            : this((IEnumerable<string>) groupIds)
        {
        }

        public DescribeGroupsRequest(IEnumerable<string> groupIds) 
            : base(ApiKey.DescribeGroups)
        {
            GroupIds = ImmutableList<string>.Empty.AddNotNullRange(groupIds);
        }

        public IImmutableList<string> GroupIds { get; }

        #region Equality

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as DescribeGroupsRequest);
        }

        /// <inheritdoc />
        public bool Equals(DescribeGroupsRequest other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return base.Equals(other) 
                   && GroupIds.HasEqualElementsInOrder(other.GroupIds);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked {
                return (base.GetHashCode()*397) ^ (GroupIds?.GetHashCode() ?? 0);
            }
        }

        /// <inheritdoc />
        public static bool operator ==(DescribeGroupsRequest left, DescribeGroupsRequest right)
        {
            return Equals(left, right);
        }

        /// <inheritdoc />
        public static bool operator !=(DescribeGroupsRequest left, DescribeGroupsRequest right)
        {
            return !Equals(left, right);
        }

        #endregion
    }
}