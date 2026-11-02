
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;

namespace EmployeeApi.Services;

public sealed class TelemetryService
{
    private readonly TelemetryClient _telemetryClient;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(
        TelemetryClient telemetryClient,
        ILogger<TelemetryService> logger)
    {
        _telemetryClient = telemetryClient;
        _logger = logger;
    }

    public void TrackException(
        Exception exception,
        string operation,
        int? employeeId = null)
    {
        var telemetry = new ExceptionTelemetry(exception);

        telemetry.Properties["Application"] = "EmployeeApi";
        telemetry.Properties["Operation"] = operation;
        telemetry.Properties["Environment"] =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Unknown";

        if (employeeId.HasValue)
        {
            telemetry.Properties["EmployeeId"] =
                employeeId.Value.ToString();
        }

        _telemetryClient.TrackException(telemetry);

        _logger.LogError(
            exception,
            "Employee API operation failed. Operation: {Operation}, EmployeeId: {EmployeeId}",
            operation,
            employeeId);
    }

    public void TrackEvent(
        string eventName,
        string operation,
        int? employeeId = null)
    {
        var telemetry = new EventTelemetry(eventName);

        telemetry.Properties["Application"] = "EmployeeApi";
        telemetry.Properties["Operation"] = operation;

        if (employeeId.HasValue)
        {
            telemetry.Properties["EmployeeId"] =
                employeeId.Value.ToString();
        }

        _telemetryClient.TrackEvent(telemetry);
    }
}
