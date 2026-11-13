using System.Text;
using pxdUninstaller.Core;

// pxdu: pxd Uninstaller from the command line. Everything is removed quietly; programs whose uninstaller has no
// quiet mode are reported, never started. "pxdu help" lists the commands.
Console.OutputEncoding = Encoding.UTF8;
return Cli.Run(args);
