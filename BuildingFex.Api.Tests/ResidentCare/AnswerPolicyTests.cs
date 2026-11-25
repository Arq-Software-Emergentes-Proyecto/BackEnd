using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;
using BuildingFex.Api.ResidentCare.Domain.Services;
using Xunit;

namespace BuildingFex.Api.Tests.ResidentCare;

public class AnswerPolicyTests
{
    private static readonly KnowledgeSource Source1 =
        new("upload-1", "Reglamento interno.pdf", "Contenido del reglamento.", 0.9);

    private static readonly KnowledgeSource Source2 =
        new("upload-2", "Acta de junta agosto.pdf", "Contenido de las actas de junta.", 0.7);

    private static readonly KnowledgeSource[] Sources = [Source1, Source2];

    [Fact]
    public void Without_sources_the_question_is_derived()
    {
        Assert.True(AnswerPolicy.MustDerive([]));
        Assert.False(AnswerPolicy.MustDerive([Source1]));
    }

    [Fact]
    public void Citation_line_is_removed_from_the_text_and_matched_to_a_retrieved_source()
    {
        var rawReply = "El horario de piscina es de 8am a 8pm.\nFuente: Acta de junta agosto.pdf";
        var answer = AnswerPolicy.ToAnswer(rawReply, Sources);

        Assert.False(answer.Text.Contains("Fuente", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("upload-2", answer.SourceDocumentId);
        Assert.True(answer.Grounded);
        Assert.False(answer.MustDerive);
    }

    [Fact]
    public void A_source_that_was_not_retrieved_falls_back_to_the_best_fragment()
    {
        var rawReply = "Se permite mascotas hasta 15kg.\nSource: otro-edificio.pdf";
        var answer = AnswerPolicy.ToAnswer(rawReply, Sources);

        Assert.Equal("upload-1", answer.SourceDocumentId);
    }

    [Fact]
    public void Escalate_marker_derives_and_is_not_shown_to_the_resident()
    {
        var rawReply = "Te derivo con la administración para que te ayuden con esto. [[ESCALATE_ADMIN]]";
        var answer = AnswerPolicy.ToAnswer(rawReply, Sources);

        Assert.True(answer.MustDerive);
        Assert.False(answer.Grounded);
        Assert.DoesNotContain("ESCALATE", answer.Text);
    }

    [Theory]
    [InlineData("Hay olor a gas en el pasillo", true)]
    [InlineData("There is a fire in the lobby", true)]
    [InlineData("¿A qué hora abre la piscina?", false)]
    public void Emergencies_are_detected_without_the_ai(string text, bool expected)
    {
        Assert.Equal(expected, AnswerPolicy.IsEmergency(text));
    }

    [Fact]
    public void Context_block_names_each_source_file()
    {
        var context = AnswerPolicy.BuildContext(Sources);

        Assert.Contains("--- source: Reglamento interno.pdf ---", context);
        Assert.Contains("--- source: Acta de junta agosto.pdf ---", context);
    }
}
