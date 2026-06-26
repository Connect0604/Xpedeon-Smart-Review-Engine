using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MigrationDashboard.Web.Services;

public sealed class EditSessionCircuitHandler(
    CircuitContextAccessor circuitContextAccessor,
    IEditSessionRegistry editSessionRegistry,
    IMigrationDashboardRepository repository) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        circuitContextAccessor.CircuitId = circuit.Id;
        return Task.CompletedTask;
    }

    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await RevokeEditSessionAsync(circuit, cancellationToken);
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await RevokeEditSessionAsync(circuit, cancellationToken);
    }

    private async Task RevokeEditSessionAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var userName = editSessionRegistry.GetEditorForCircuit(circuit.Id);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return;
        }

        if (!editSessionRegistry.RevokeCircuitSession(circuit.Id))
        {
            return;
        }

        await repository.RecordDisconnectAsync(userName, cancellationToken);
    }
}
