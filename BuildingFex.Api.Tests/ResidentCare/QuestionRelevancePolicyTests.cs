using BuildingFex.Api.ResidentCare.Domain.Services;

namespace BuildingFex.Api.Tests.ResidentCare;

public class QuestionRelevancePolicyTests
{
    [Theory]
    [InlineData("hola")]
    [InlineData("¡Gracias!")]
    public void Greetings_and_thanks_do_not_use_the_ai(string text) =>
        Assert.Equal(QuestionScope.SmallTalk, QuestionRelevancePolicy.Classify(text));

    [Theory]
    [InlineData("?")]
    [InlineData("😀😀")]
    public void Messages_without_content_ask_to_rephrase(string text) =>
        Assert.Equal(QuestionScope.Unclear, QuestionRelevancePolicy.Classify(text));

    [Theory]
    [InlineData("¿Cuál es el horario de la piscina?")]
    [InlineData("¿Cómo reporto una fuga de agua en mi departamento?")]
    public void Building_questions_go_to_the_assistant(string text) =>
        Assert.Equal(QuestionScope.Building, QuestionRelevancePolicy.Classify(text));
}
