using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentDesk.Domain;

namespace AgentDesk.Infrastructure;

/// <summary>Turn a missing variant tag into an input error instead of abstract-type construction failure.</summary>
public sealed class RequiredDiscriminatorResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var info = base.GetTypeInfo(type, options);
        if (type == typeof(QuestionState) || type == typeof(QuestionDecision)
            || type == typeof(DeliveryState) || type == typeof(DispatchContent)
            || type == typeof(WorkOwner) || type == typeof(WorkState))
        {
            info.CreateObject = static () => throw new JsonException("Das Variantenfeld 'kind' ist erforderlich.");
        }
        return info;
    }
}
