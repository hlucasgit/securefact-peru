namespace SecureFact.Security.Tests;

/// <summary>
/// The container images of the tests. <c>SF_IMAGE_REGISTRY</c> (for example <c>mirror.gcr.io/</c>) is put before the name, so the CI can pull through a mirror when the shared runners exhaust the
/// anonymous limit of Docker Hub; empty, the images come from Docker Hub as always. The names of the official images carry <c>library/</c> so the prefix gives a valid reference.
/// </summary>
internal static class TestImages
{
    public static string Name(string image) => (Environment.GetEnvironmentVariable("SF_IMAGE_REGISTRY") ?? string.Empty) + image;
}
