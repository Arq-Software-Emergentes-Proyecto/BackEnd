using System.Text;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

/// <summary>Servicio de dominio: el asistente solo responde con fuentes vigentes del mismo edificio o la guía de uso.</summary>
public static class AnswerPolicy
{
    public static string BuildContext(IReadOnlyList<KnowledgeSource> sources)
    {
        if (sources.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var source in sources)
        {
            sb.Append("--- source: ").Append(source.FileName).AppendLine(" ---");
            sb.AppendLine(source.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string NoDocumentaryBasisMessage(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "I couldn't find a documentary basis in your building's current bylaws or meeting minutes to answer this, so I can't reply without guessing."
            : "No encontré una base documental en el reglamento ni en las actas vigentes de tu edificio para responder esto, así que no puedo contestarte sin adivinar.";
}
