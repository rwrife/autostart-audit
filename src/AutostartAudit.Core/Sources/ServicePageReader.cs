using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutostartAudit.Core.Model;

[assembly: InternalsVisibleTo("AutostartAudit.Core.Tests")]

namespace AutostartAudit.Core.Scan;

internal sealed record ServicePage<T>(IReadOnlyList<T> Records, uint Resume, int Error);

/// <summary>Consumes SCM pages, including records returned with ERROR_MORE_DATA.</summary>
internal static class ServicePageReader
{
    public static void Read<T>(Func<uint, ServicePage<T>> fetch, Action<T> visit, List<SourceObservation> observations)
    {
        uint resume = 0;
        var seen = new HashSet<uint> { resume };
        while (true)
        {
            ServicePage<T> page;
            try
            {
                page = fetch(resume);
            }
            catch (Exception ex)
            {
                observations.Add(new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Unknown,
                    Detail = $"SCM continuation at {resume} failed: {ex.GetType().Name}: {ex.Message}; earlier records retained" });
                return;
            }
            if (page.Error is 0 or 234)
                foreach (var record in page.Records) visit(record);
            if (page.Error == 0)
            {
                observations.Add(new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Ok });
                return;
            }
            if (page.Error != 234)
            {
                observations.Add(new SourceObservation { Id = "service-control-manager", Health = page.Error == 5 ? ObservationHealth.Denied : ObservationHealth.Unknown,
                    Detail = $"SCM continuation at {resume} failed: Win32 error {page.Error}: {new Win32Exception(page.Error).Message}" });
                return;
            }
            if (page.Records.Count == 0 || !seen.Add(page.Resume))
            {
                observations.Add(new SourceObservation { Id = "service-control-manager", Health = ObservationHealth.Unknown,
                    Detail = "SCM enumeration returned more data without a progressing continuation; earlier records retained" });
                return;
            }
            resume = page.Resume;
        }
    }
}
