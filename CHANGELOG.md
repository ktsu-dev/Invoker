## v1.1.3-pre.1 (prerelease)

Changes since v1.1.2:

- Sync global.json ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Merge remote-tracking branch 'refs/remotes/origin/main' ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Sync .github\workflows\dotnet.yml ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Merge remote-tracking branch 'refs/remotes/origin/main' ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Sync .gitignore ([@ktsu[bot]](https://github.com/ktsu[bot]))
## v1.1.2 (patch)

Changes since v1.1.1:

- migrate to dotnet 10 ([@matt-edmondson](https://github.com/matt-edmondson))
- migrate to dotnet 10 ([@matt-edmondson](https://github.com/matt-edmondson))
## v1.1.2-pre.4 (prerelease)

Changes since v1.1.2-pre.3:

- Sync scripts\update-winget-manifests.ps1 ([@ktsu[bot]](https://github.com/ktsu[bot]))
## v1.1.2-pre.3 (prerelease)

Changes since v1.1.2-pre.2:

- Sync .github\workflows\dotnet.yml ([@ktsu[bot]](https://github.com/ktsu[bot]))
## v1.1.2-pre.2 (prerelease)

Changes since v1.1.2-pre.1:

- Sync .github\workflows\dotnet.yml ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Sync scripts\PSBuild.psm1 ([@ktsu[bot]](https://github.com/ktsu[bot]))
## v1.1.2-pre.1 (prerelease)

Incremental prerelease update.
## v1.1.1 (patch)

Changes since v1.1.0:

- Remove Directory.Build.props and Directory.Build.targets files, delete unused PowerShell scripts, and add copyright headers to Invoker and InvokerTests classes. ([@matt-edmondson](https://github.com/matt-edmondson))
- Refactor project files and configurations for .NET 9 compatibility. Update indentation settings in .editorconfig, adjust .gitattributes and .gitignore for consistency, and remove deprecated polyfill code. Enhance Invoker class methods with Guard checks for null arguments. ([@matt-edmondson](https://github.com/matt-edmondson))
- Enhance .NET workflow with manual trigger support, update build steps for SonarQube integration, and improve error handling in PSBuild scripts. Adjust project files for .NET 9 compatibility and refine test cases for better exception handling. ([@matt-edmondson](https://github.com/matt-edmondson))
- Update project metadata and enhance documentation with detailed features and usage examples for the ktsu.Invoker library. ([@matt-edmondson](https://github.com/matt-edmondson))
- Refactor asynchronous task execution in InvokerTests to use Thread instead of Task.Run, ensuring tasks are queued correctly before assertions. ([@matt-edmondson](https://github.com/matt-edmondson))
- Update configuration files and scripts for improved build and test processes ([@matt-edmondson](https://github.com/matt-edmondson))
## v1.1.1-pre.2 (prerelease)

Changes since v1.1.1-pre.1:

- Sync .github\workflows\dotnet.yml ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Sync .editorconfig ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Sync .runsettings ([@ktsu[bot]](https://github.com/ktsu[bot]))
## v1.1.1-pre.1 (prerelease)

Incremental prerelease update.
## v1.1.0 (minor)

Changes since v1.0.0:

- Update changelog script to include additional version check for the initial version ([@matt-edmondson](https://github.com/matt-edmondson))
- Add LICENSE template ([@matt-edmondson](https://github.com/matt-edmondson))
## v1.0.1-pre.2 (prerelease)

Changes since v1.0.1-pre.1:

- Sync scripts\make-changelog.ps1 ([@ktsu[bot]](https://github.com/ktsu[bot]))
- Sync scripts\make-version.ps1 ([@ktsu[bot]](https://github.com/ktsu[bot]))
## v1.0.1-pre.1 (prerelease)

Changes since v1.0.0:

- Update changelog script to include additional version check for the initial version ([@matt-edmondson](https://github.com/matt-edmondson))
## v1.0.0 (major)

No significant changes detected since v0.0.1.
## v0.0.1 (patch)

- Update sample project file to disable packing ([@matt-edmondson](https://github.com/matt-edmondson))
- Update changelog script to include additional version check for the initial version ([@matt-edmondson](https://github.com/matt-edmondson))
- Initial commit ([@matt-edmondson](https://github.com/matt-edmondson))
- Update README.md for ktsu.Invoker usage examples ([@matt-edmondson](https://github.com/matt-edmondson))
- Update README.md with improved usage examples ([@matt-edmondson](https://github.com/matt-edmondson))
- Remove exclusion of LICENSE.md from project ([@matt-edmondson](https://github.com/matt-edmondson))
- Fix DoInvokesSameThreadShouldExecuteAllTasks test ([@matt-edmondson](https://github.com/matt-edmondson))
- Update Sample.csproj to executable output type ([@matt-edmondson](https://github.com/matt-edmondson))
