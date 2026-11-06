# Third-party components

Hindsight's own code is MIT licensed (see LICENSE). It ships with:

- **Microsoft Edge WebView2 SDK** 1.0.3856.49 (`lib/Microsoft.Web.WebView2.Core.dll`, `lib/Microsoft.Web.WebView2.WinForms.dll`, `lib/WebView2Loader.*.dll`), redistributed under the [WebView2 SDK license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3856.49/license). These files are embedded in Hindsight.exe.
- **Bricolage Grotesque** by Mathieu Triay and the Bricolage Grotesque Project Authors, under the SIL Open Font License 1.1 (`ui/bricolage-OFL.txt`).

Hindsight controls [OBS Studio](https://obsproject.com) (GPL-2.0) as a separate program over its websocket API. It doesn't include or link to any OBS code.
