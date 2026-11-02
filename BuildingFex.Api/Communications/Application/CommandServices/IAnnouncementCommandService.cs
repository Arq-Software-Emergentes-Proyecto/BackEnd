using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Domain.Model.Commands;
using BuildingFex.Api.Shared.Application.Model;

namespace BuildingFex.Api.Communications.Application.CommandServices;

public interface IAnnouncementCommandService
{
    Task<Result<Announcement>> Handle(CreateAnnouncementCommand command, CancellationToken cancellationToken = default);
    Task<Result<Announcement>> Handle(UpdateAnnouncementCommand command, CancellationToken cancellationToken = default);
    Task<Result<Announcement>> Handle(DeleteAnnouncementCommand command, CancellationToken cancellationToken = default);
}
