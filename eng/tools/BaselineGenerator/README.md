# BaselineGenerator

This tool is used to generate an MSBuild file which sets the "baseline" against which servicing updates are built.

## Usage

Add `--package-source {source}` to the commands below if the packages of interest are not all hosted on NuGet.org.

### Auto-update

1. Run `dotnet run --update` in this project folder.
2. Run `dotnet run` in this project.

### Manual update

1. Add to the [Baseline.xml](/eng/Baseline.xml) a list of package ID's and their latest released versions. The source of
this information can typically be found in the build.xml file generated during ProdCon builds. See
<https://github.com/dotnet/versions/blob/master/build-info/dotnet/product/cli/release/2.2.4/build.xml> for example.
Update the version at the top of baseline.xml to match prior release (even if no packages changed in the prior release).
2. Run `dotnet run` on this project.
