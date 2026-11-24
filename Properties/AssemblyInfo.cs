using Rhino.PlugIns;
using System.Runtime.InteropServices;

// Plug-in Description Attributes - shown in Rhino's Options > Plug-ins page.
[assembly: PlugInDescription(DescriptionType.Address, "")]
[assembly: PlugInDescription(DescriptionType.Country, "")]
[assembly: PlugInDescription(DescriptionType.Email, "")]
[assembly: PlugInDescription(DescriptionType.Phone, "")]
[assembly: PlugInDescription(DescriptionType.Fax, "")]
[assembly: PlugInDescription(DescriptionType.Organization, "")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "")]
[assembly: PlugInDescription(DescriptionType.WebSite, "")]
[assembly: PlugInDescription(DescriptionType.Icon, "Chameleon.EmbeddedResources.plugin-utility.ico")]

// The Guid of the Rhino plug-in (must be unique to this plug-in).
[assembly: Guid("75cd5bd0-536e-4c10-a1f7-c9137e65abb1")]
