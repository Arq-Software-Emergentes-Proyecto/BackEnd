using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;
using BuildingFex.Api.ResidentCare.Domain.Services;

namespace BuildingFex.Api.Tests.ResidentCare;

public class AnswerPolicyTests
{
    private static readonly KnowledgeSource Rules = new("upload-1", "Reglamento interno.pdf", "Horario de silencio de 23:00 a 7:00.", 0.9);
    private static readonly KnowledgeSource Minutes = new("upload-2", "Acta de junta agosto.pdf", "Se aprobó pintar la fachada.", 0.7);

    [Fact]
    public void Without_sources_the_question_is_derived()
    {
        Assert.True(AnswerPolicy.MustDerive([]));
        Assert.False(AnswerPolicy.MustDerive([Rules]));
    }

    [Fact]
    public void Citation_line_is_removed_from_the_text_and_matched_to_a_retrieved_source()
    {
        var answer = AnswerPolicy.ToAnswer("El silencio es de 23:00 a 7:00.\nFuente: Acta de junta agosto.pdf", [Rules, Minutes]);

        Assert.Equal("El silencio es de 23:00 a 7:00.", answer.Text);
        Assert.Equal("upload-2", answer.SourceDocumentId);
        Assert.True(answer.Grounded);
        Assert.False(answer.MustDerive);
    }

    [Fact]
    public void A_source_that_was_not_retrieved_falls_back_to_the_best_fragment()
    {
        var answer = AnswerPolicy.ToAnswer("Respuesta.\nSource: otro-edificio.pdf", [Rules]);

        Assert.Equal("upload-1", answer.SourceDocumentId);
    }

    [Fact]
    public void Escalate_marker_derives_and_is_not_shown_to_the_resident()
    {
        var answer = AnswerPolicy.ToAnswer("No tengo esa información.\n[[ESCALATE_ADMIN]]", [Rules]);

        Assert.True(answer.MustDerive);
        Assert.False(answer.Grounded);
        Assert.DoesNotContain("ESCALATE", answer.Text);
    }

    [Theory]
    [InlineData("Hay olor a gas en el pasillo", true)]
    [InlineData("There is a fire in the lobby", true)]
    [InlineData("¿A qué hora abre la piscina?", false)]
    public void Emergencies_are_detected_without_the_ai(string text, bool expected) =>
        Assert.Equal(expected, AnswerPolicy.IsEmergency(text));

    [Fact]
    public void Context_block_names_each_source_file()
    {
        var context = AnswerPolicy.BuildContext([Rules, Minutes]);

        Assert.Contains("--- source: Reglamento interno.pdf ---", context);
        Assert.Contains("--- source: Acta de junta agosto.pdf ---", context);
    }
}
