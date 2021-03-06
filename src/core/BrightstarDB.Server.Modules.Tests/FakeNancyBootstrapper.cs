﻿using BrightstarDB.Client;
using BrightstarDB.Server.Modules.Permissions;
using Nancy;

namespace BrightstarDB.Server.Modules.Tests
{
    public class FakeNancyBootstrapper : DefaultNancyBootstrapper
    {
        private readonly IBrightstarService _brightstarService;
        private readonly AbstractStorePermissionsProvider _storePermissionsProvider;
        private readonly AbstractSystemPermissionsProvider _systemPermissionsProvider;

        public FakeNancyBootstrapper(IBrightstarService brightstarService) : this(
            brightstarService, new FallbackStorePermissionsProvider(StorePermissions.All, StorePermissions.All))
        {
        }

        public FakeNancyBootstrapper(IBrightstarService brightstarService,
                                     AbstractStorePermissionsProvider storePermissionsProvider)
            : this(brightstarService, storePermissionsProvider, new FallbackSystemPermissionsProvider(SystemPermissions.All))
        {
            
        }

        public FakeNancyBootstrapper(IBrightstarService brightstarService,
                                     AbstractStorePermissionsProvider storePermissionsProvider,
            AbstractSystemPermissionsProvider systemPermissionsProvider)
        {
            _brightstarService = brightstarService;
            _storePermissionsProvider = storePermissionsProvider;
            _systemPermissionsProvider = systemPermissionsProvider;
        }

        protected override void ConfigureApplicationContainer(Nancy.TinyIoc.TinyIoCContainer container)
        {
            base.ConfigureApplicationContainer(container);
            container.Register<IBrightstarService>(_brightstarService);
            container.Register<AbstractStorePermissionsProvider>(_storePermissionsProvider);
            container.Register(_systemPermissionsProvider);
        }

        protected override IRootPathProvider RootPathProvider
        {
            get
            {
                return new DefaultRootPathProvider();
            }
        }
    }
}
