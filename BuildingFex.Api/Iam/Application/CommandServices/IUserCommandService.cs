using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.Commands;
using BuildingFex.Api.Shared.Application.Model;

namespace BuildingFex.Api.Iam.Application.CommandServices;

public interface IUserCommandService
{
    Task<Result<(User user, string token)>> Handle(SignInCommand command, CancellationToken cancellationToken = default);
    Task<Result<(User user, string token)>> Handle(SignInGoogleCommand command, CancellationToken cancellationToken = default);
    Task<Result<(User user, string token)>> Handle(CompleteResidentGoogleCommand command, CancellationToken cancellationToken = default);
    Task<Result<(User user, string token)>> Handle(RegisterAdminCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(UpsertFirebaseAdminCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(CreateResidentCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(UpdateResidentCredentialsCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(PatchResidentCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(DeleteResidentCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(DeactivateAccountCommand command, CancellationToken cancellationToken = default);
    Task<Result<User>> Handle(ReactivateAccountCommand command, CancellationToken cancellationToken = default);
}
