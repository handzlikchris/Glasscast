using GlassesRemote.Server.Hosting;
using Microsoft.AspNetCore.Http;

namespace GlassesRemote.Server.Tests.Hosting;

public sealed class ClientCachingTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/favicon.svg")]
    [InlineData("/assetsfoo.js")]
    public void The_page_and_unhashed_files_are_revalidated_on_every_load(string path) =>
        Assert.Equal("no-cache", ClientCaching.For(new PathString(path)));

    [Theory]
    [InlineData("/assets/index-CdHwXCFK.js")]
    [InlineData("/assets/index-BEHPoCT2.css")]
    public void Hashed_assets_are_cached_for_good(string path) =>
        Assert.Equal("public, max-age=31536000, immutable", ClientCaching.For(new PathString(path)));
}
