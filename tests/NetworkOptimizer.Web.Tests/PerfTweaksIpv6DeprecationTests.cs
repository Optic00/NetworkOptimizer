using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The IPv6 prefix deprecation tweak's status, read from the boot script, the systemd unit and
/// the daemon's RAM-only status file.
/// </summary>
public class PerfTweaksIpv6DeprecationTests
{
    private const long GatewayNow = 1_793_000_000;
    private const string Id = "ipv6-prefix-deprecation";

    private static Dictionary<string, string> Deployed(string state = "ready", int networks = 3, int deprecating = 0,
        long heartbeat = GatewayNow - 5, string error = "null", string lastEvent = "null") => new()
    {
        ["GATEWAY_MODEL"] = "UCGF",
        ["FIRMWARE_VERSION"] = "6.0.16",
        ["IPV6_DEP_BOOT_SCRIPT"] = "exists",
        ["IPV6_DEP_SERVICE"] = "active",
        ["IPV6_DEP_STATUS"] = $$"""{"state": "{{state}}", "networks": {{networks}}, "deprecating": {{deprecating}}, "last_send": null, "last_event": {{lastEvent}}, "error": {{error}}, "heartbeat": {{heartbeat}}}""",
        ["IPV6_DEP_NOW"] = GatewayNow.ToString(),
    };

    private static TweakDeploymentStatus Parse(Dictionary<string, string> sections)
    {
        var output = string.Join("\n", sections.Select(kv => $"---{kv.Key}---\n{kv.Value}"));
        var status = new PerfTweaksStatus();
        PerfTweaksDeploymentService.ParseStatusOutput(output, status, DateTime.UtcNow);
        return status.Tweaks[Id];
    }

    [Fact]
    public void NotDeployed_HasNoHealthChecks()
    {
        var tweak = Parse(new()
        {
            ["IPV6_DEP_BOOT_SCRIPT"] = "missing",
            ["IPV6_DEP_SERVICE"] = "inactive",
            ["IPV6_DEP_STATUS"] = "missing",
            ["IPV6_DEP_NOW"] = GatewayNow.ToString(),
        });

        tweak.BootScriptDeployed.Should().BeFalse();
        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().BeEmpty();
    }

    [Fact]
    public void Running_WithFreshStatus_IsActive()
    {
        var tweak = Parse(Deployed());

        tweak.IsActive.Should().BeTrue();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Networks", "3 watched", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Old Prefixes", "None", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().NotContain(h => h.Status != HealthCheckStatus.Ok);
    }

    [Fact]
    public void Deprecating_ReportsCountAndLastEvent()
    {
        var tweak = Parse(Deployed("deprecating", deprecating: 2, lastEvent: "\"br0: old replaced by new, deprecating\""));

        tweak.IsActive.Should().BeTrue();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Old Prefixes", "2 being deprecated", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Last Event", "br0: old replaced by new, deprecating", HealthCheckStatus.Ok));
    }

    [Fact]
    public void StaleHeartbeat_IsNotActive()
    {
        var tweak = Parse(Deployed(heartbeat: GatewayNow - 300));

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Status", "Not updating", HealthCheckStatus.Error));
    }

    [Fact]
    public void ServiceStopped_WithBootScript_IsAnError()
    {
        var sections = Deployed();
        sections["IPV6_DEP_SERVICE"] = "inactive";
        sections["IPV6_DEP_STATUS"] = "missing";

        var tweak = Parse(sections);

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().ContainSingle().Which.Should().Be(new HealthCheckResult("Service", "Not running", HealthCheckStatus.Error));
    }

    [Fact]
    public void DaemonError_IsShownAsIssue()
    {
        var tweak = Parse(Deployed("error", error: "\"snapshot failed: TimeoutExpired()\""));

        tweak.IsActive.Should().BeFalse();
        tweak.IssueDescription.Should().Be("snapshot failed: TimeoutExpired()");
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Status", "Error", HealthCheckStatus.Error));
    }

    [Fact]
    public void NoSlaacNetwork_IsAWarning()
    {
        var tweak = Parse(Deployed("waiting", networks: 0));

        tweak.IsActive.Should().BeTrue();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Networks", "No SLAAC network found", HealthCheckStatus.Warning));
    }

    [Fact]
    public void ChangedBootScript_IsOutdated()
    {
        var sections = Deployed();
        sections["SCRIPT_HASHES"] = "30-ipv6-prefix-deprecation.sh:00000000000000000000000000000000";

        Parse(sections).ScriptOutdated.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAllStatusAsync_HashesTheBootScript()
    {
        string? command = null;
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((c, _, _) => command = c)
            .ReturnsAsync((false, "unreachable"));

        await new PerfTweaksDeploymentService(Mock.Of<ILogger<PerfTweaksDeploymentService>>(), ssh.Object, null!, null!, null!, null!,
            new NetworkOptimizer.Web.Services.Firmware.RolloutSuppressionRegistry()).CheckAllStatusAsync();

        var hashLoop = command!.Split("---SCRIPT_HASHES---")[1].Split("done;")[0];
        hashLoop.Should().Contain(" 30-ipv6-prefix-deprecation.sh;");
    }
}
