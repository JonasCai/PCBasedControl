using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Controller.EventLogger;

[BsonKnownTypes(typeof(AlarmModel), typeof(MessageModel))]
public record class EventBase
{
    public required int EventId { get; init; }
    [BsonRepresentation(BsonType.String)]
    public required SeverityLevel Severity { get; init; }
    public required string MessageTemplate { get; init; }
}

public record class AlarmModel : EventBase
{
    [BsonId]
    public Guid InstanceId { get; init; }
    public required string SourceName { get; init; }
    public DateTime TimeRaised { get; init; }
    public DateTime TimeCleared { get; init; }
    public required string Message { get; init; }
    [BsonRepresentation(BsonType.String)]
    public AlarmState State { get; init; }
}

public record class MessageModel : EventBase
{
    [BsonId]
    public Guid InstanceId { get; init; }
    public required string SourceName { get; init; }
    public DateTime TimeRaised { get; init; }
    public required string Message { get; init; }
}

public enum SeverityLevel
{
    Info,
    Warning,
    Error
}

public enum AlarmState
{
    Arrived,
    Left
}
