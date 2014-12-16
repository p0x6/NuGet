﻿using Microsoft.VisualStudio.Shell;
using NuGet.Versioning;
using NuGet.VisualStudio;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;


#if VS14
using Microsoft.VisualStudio.ProjectSystem.Interop;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
#endif

namespace NuGet.Client.VisualStudio.PowerShell
{
    /// <summary>
    /// This command installs the specified package into the specified project.
    /// </summary>
    /// TODO List
    /// 1. Filter unlisted packages from latest version, if version is not specified by user
    /// 2. Add new path/package recognition feature
    /// 3. Add back WriteDisClaimer before installing packages. Should be one of the Resolver actions.
    /// 4. Add back popping up Readme.txt feature. Should be one of the Resolver actions. 
    /// 5. Implement Add-BindingRedirect for V3
    [Cmdlet(VerbsLifecycle.Install, "Package2")]
    public class InstallPackageCommand : PackageInstallBaseCommand
    {
        private bool _readFromPackagesConfig;
        private bool _readFromDirectPackagePath;
        private bool _isNetworkAvailable;
        private string _fallbackToLocalCacheMessge = Resources.Cmdlet_FallbackToCache;
        private string _localCacheFailureMessage = Resources.Cmdlet_LocalCacheFailure;
        private string _cacheStatusMessage = String.Empty;
        private object _currentSource = String.Empty;

        public InstallPackageCommand() :
            base(ServiceLocator.GetInstance<IVsPackageSourceProvider>(),
                 ServiceLocator.GetInstance<IPackageRepositoryFactory>(),
                 ServiceLocator.GetInstance<SVsServiceProvider>(),
                 ServiceLocator.GetInstance<IVsPackageManagerFactory>(),
                 ServiceLocator.GetInstance<ISolutionManager>(),
                 ServiceLocator.GetInstance<IHttpClientEvents>())
        {
            _isNetworkAvailable = isNetworkAvailable();
        }

        protected override void BeginProcessing()
        {
            FallbackToCacheIfNeccessary();
            base.BeginProcessing();
        }

        private static bool isNetworkAvailable()
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }

        protected override void Preprocess()
        {
            base.Preprocess();
            ParseUserInputForId();
            this.Identities = GetIdentitiesForResolver();
        }

        /// <summary>
        /// Parse user input for Id parameter. 
        /// Id can be the name of a package, path to packages.config file or path to .nupkg file.
        /// </summary>
        private void ParseUserInputForId()
        {
            if (!string.IsNullOrEmpty(Id))
            {
                if (Id.ToLowerInvariant().EndsWith(NuGet.Constants.PackageReferenceFile))
                {
                    _readFromPackagesConfig = true;
                }
                else if (Id.ToLowerInvariant().EndsWith(NuGet.Constants.PackageExtension))
                {
                    _readFromDirectPackagePath = true;
                }
            }
        }

        /// <summary>
        /// Get Identities for Resolver. Can be a single Identity for Install/Uninstall-Package.
        /// or multiple identities for Install/Update-Package.
        /// </summary>
        /// <returns></returns>
        protected IEnumerable<PackageIdentity> GetIdentitiesForResolver()
        {
            IEnumerable<PackageIdentity> identityList = Enumerable.Empty<PackageIdentity>();
            if (_readFromPackagesConfig)
            {
                identityList = CreatePackageIdentitiesFromPackagesConfig();
            }
            else if (_readFromDirectPackagePath)
            {
                identityList = CreatePackageIdentityFromNupkgPath();
            }
            else
            {
                identityList = GetPackageIdentityForResolver();
            }
            return identityList;
        }

        private void FallbackToCacheIfNeccessary()
        {
            /**** Fallback to Cache logic***/
            //1. Check if there is any http source (in active sources or Source switch)
            //2. Check if any one of the UNC or local sources is available (in active sources)
            //3. If none of the above is true, fallback to cache

            //Check if any of the active package source is available. This function will return true if there is any http source in active sources
            //For http sources, we will continue and fallback to cache at a later point if the resource is unavailable

            if (String.IsNullOrEmpty(Source))
            {
                bool isAnySourceAvailable = false;
                _currentSource = ActiveSourceRepository;
                isAnySourceAvailable = UriHelper.IsAnySourceAvailable(PackageSourceProvider, _isNetworkAvailable);

                //if no local or UNC source is available or no source is http, fallback to local cache
                if (!isAnySourceAvailable)
                {
                    Source = NuGet.MachineCache.Default.Source;
                    CacheStatusMessage(_currentSource, Source);
                }
            }

            //At this point, Source might be value from -Source switch or NuGet Local Cache
            /**** End of Fallback to Cache logic ***/
        }

        private void CacheStatusMessage(object currentSource, string cacheSource)
        {
            if (!String.IsNullOrEmpty(cacheSource))
            {
                _cacheStatusMessage = String.Format(CultureInfo.CurrentCulture, _fallbackToLocalCacheMessge, currentSource, Source);
            }
            else
            {
                _cacheStatusMessage = String.Format(CultureInfo.CurrentCulture, _localCacheFailureMessage, currentSource);
            }

            Log(MessageLevel.Warning, String.Format(CultureInfo.CurrentCulture, _cacheStatusMessage, PackageSourceProvider.ActivePackageSource, Source));
        }

        /// <summary>
        /// Returns single package identity for resolver when Id is specified
        /// </summary>
        /// <returns></returns>
        private List<PackageIdentity> GetPackageIdentityForResolver()
        {
            PackageIdentity identity = null;

            // If Version is specified by commandline parameter
            if (!string.IsNullOrEmpty(Version))
            {
                NuGetVersion nVersion = ParseUserInputForVersion(Version);
                PackageIdentity pIdentity = new PackageIdentity(Id, nVersion);
                if (!_readFromDirectPackagePath)
                {
                    identity = Client.PackageRepositoryHelper.ResolvePackage(ActiveSourceRepository, V2LocalRepository, pIdentity, IncludePrerelease.IsPresent);
                }
            }
            else
            {
                // Get the latest Version from package repository.
                IEnumerable<FrameworkName> frameworks = this.Projects.FirstOrDefault().GetSupportedFrameworks();
                Version = PowerShellPackage.GetLastestVersionForPackage(ActiveSourceRepository, Id, frameworks, IncludePrerelease.IsPresent);
                identity = new PackageIdentity(Id, NuGetVersion.Parse(Version));
            }

            return new List<PackageIdentity>() { identity };
        }

        /// <summary>
        /// Return list of package identities parsed from packages.config
        /// </summary>
        /// <returns></returns>
        private IEnumerable<PackageIdentity> CreatePackageIdentitiesFromPackagesConfig()
        {
            List<PackageIdentity> identities = new List<PackageIdentity>();
            IEnumerable<PackageIdentity> parsedIdentities = null;

            try
            {
                // Example: install-package2 https://raw.githubusercontent.com/NuGet/json-ld.net/master/src/JsonLD/packages.config
                if (Id.ToLowerInvariant().StartsWith("http"))
                {
                    string text = ReadPackagesConfigFileContentOnline(Id).Replace("???", "");
                    PackagesConfigReader reader = new PackagesConfigReader(text);
                    parsedIdentities = reader.GetPackages();
                }
                else
                {
                    using (FileStream stream = new FileStream(Id, FileMode.Open))
                    {
                        PackagesConfigReader reader = new PackagesConfigReader(stream);
                        parsedIdentities = reader.GetPackages();
                        if (stream != null)
                        {
                            stream.Close();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(MessageLevel.Error, Resources.Cmdlet_FailToParsePackages, Id, ex.Message);
            }

            foreach (PackageIdentity identity in parsedIdentities)
            {
                PackageIdentity resolvedIdentity = Client.PackageRepositoryHelper.ResolvePackage(ActiveSourceRepository, V2LocalRepository, identity, IncludePrerelease.IsPresent);
                identities.Add(resolvedIdentity);
            }
            return identities;
        }

        private IEnumerable<PackageIdentity> CreatePackageIdentityFromNupkgPath()
        {
            PackageIdentity identity = null;
            if (UriHelper.IsHttpSource(Id))
            {
                throw new NotImplementedException();
            }
            else
            {
                try
                {
                    string fullPath = Path.GetFullPath(Id);
                    Source = Path.GetDirectoryName(fullPath);
                    var package = new OptimizedZipPackage(fullPath);
                    if (package != null)
                    {
                        Id = package.Id;
                        Version = package.Version.ToString();
                    }
                    identity = new PackageIdentity(Id, NuGetVersion.Parse(Version));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }
            return new List<PackageIdentity>() { identity };
        }

        /// <summary>
        /// Read the content of the file via HttpWebRequest
        /// </summary>
        /// <param name="url"></param>
        private string ReadPackagesConfigFileContentOnline(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();

            // Read data via the response stream
            Stream resStream = response.GetResponseStream();
            string tempString = null;
            StringBuilder stringBuilder = new StringBuilder();

            int bytesToRead = 10000;
            byte[] buffer = new Byte[bytesToRead];
            int count = 0;

            do
            {
                // Fill the buffer with data
                count = resStream.Read(buffer, 0, buffer.Length);

                // Make sure we read some data
                if (count != 0)
                {
                    // Translate from bytes to ASCII text
                    tempString = Encoding.ASCII.GetString(buffer, 0, count);

                    // Continue building the string
                    stringBuilder.Append(tempString);
                }
            }
            while (count > 0); // Any more data to read?
            resStream.Close();

            // Return content
            return stringBuilder.ToString();
        }
    }
}