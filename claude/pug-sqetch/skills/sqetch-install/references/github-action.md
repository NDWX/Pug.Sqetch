# Installing sqetch-deploy in GitHub Actions

The `install-sqetch-deploy` action downloads a `sqetch-deploy` release for the runner (Linux, macOS
or Windows, x64 or arm64), checks it against the release's `SHA256SUMS`, and puts it on `PATH` for
the rest of the job:

```yaml
steps:
  - uses: NDWX/Pug.Sqetch/actions/install-sqetch-deploy@sqetch_deploy-v0.1.0
    with:
      version: 0.1.0
  - run: sqetch-deploy --version
```

- `version` is required: the tool's version as its release files name it. A pre-release is written
  in full, such as `0.1.0-pre.202610172239`.
- The `@ref` pins the action's own code, separately from `version`. Use a release tag or a commit
  SHA, never a branch.
- Outputs: `path` (the folder holding the executable) and `version`.
- It needs `bash` and `curl`, plus `tar` on Linux and macOS and Windows PowerShell on Windows. GitHub's
  hosted runners have them all. A self-hosted runner keeps each version in its tool cache.

There is no action for `sqetch`. A job that also needs `sqetch` uses mise (`jdx/mise-action`) or
`dotnet tool restore`; see `sqetch-ci`.
