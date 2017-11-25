﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Acr.UserDialogs;
using Bit.App.Abstractions;
using Bit.App.Controls;
using Bit.App.Models.Page;
using Bit.App.Resources;
using Xamarin.Forms;
using XLabs.Ioc;
using Bit.App.Utilities;
using Plugin.Settings.Abstractions;
using Plugin.Connectivity.Abstractions;
using System.Collections.Generic;
using System.Threading;
using Bit.App.Enums;

namespace Bit.App.Pages
{
    public class VaultListGroupingsPage : ExtendedContentPage
    {
        private readonly IFolderService _folderService;
        private readonly ICollectionService _collectionService;
        private readonly ICipherService _cipherService;
        private readonly IUserDialogs _userDialogs;
        private readonly IConnectivity _connectivity;
        private readonly IDeviceActionService _deviceActionService;
        private readonly ISyncService _syncService;
        private readonly IPushNotificationService _pushNotification;
        private readonly IDeviceInfoService _deviceInfoService;
        private readonly ISettings _settings;
        private readonly IAppSettingsService _appSettingsService;
        private readonly IGoogleAnalyticsService _googleAnalyticsService;
        private CancellationTokenSource _filterResultsCancellationTokenSource;

        public VaultListGroupingsPage()
            : base(true)
        {
            _folderService = Resolver.Resolve<IFolderService>();
            _collectionService = Resolver.Resolve<ICollectionService>();
            _cipherService = Resolver.Resolve<ICipherService>();
            _connectivity = Resolver.Resolve<IConnectivity>();
            _userDialogs = Resolver.Resolve<IUserDialogs>();
            _deviceActionService = Resolver.Resolve<IDeviceActionService>();
            _syncService = Resolver.Resolve<ISyncService>();
            _pushNotification = Resolver.Resolve<IPushNotificationService>();
            _deviceInfoService = Resolver.Resolve<IDeviceInfoService>();
            _settings = Resolver.Resolve<ISettings>();
            _appSettingsService = Resolver.Resolve<IAppSettingsService>();
            _googleAnalyticsService = Resolver.Resolve<IGoogleAnalyticsService>();

            Init();
        }

        public ExtendedObservableCollection<VaultListPageModel.Section> PresentationSections { get; private set; }
            = new ExtendedObservableCollection<VaultListPageModel.Section>();
        public ListView ListView { get; set; }
        public SearchBar Search { get; set; }
        public StackLayout NoDataStackLayout { get; set; }
        public StackLayout ResultsStackLayout { get; set; }
        public ActivityIndicator LoadingIndicator { get; set; }
        private AddCipherToolBarItem AddCipherItem { get; set; }

        private void Init()
        {
            AddCipherItem = new AddCipherToolBarItem(this);
            ToolbarItems.Add(AddCipherItem);

            ListView = new ListView(ListViewCachingStrategy.RecycleElement)
            {
                IsGroupingEnabled = true,
                ItemsSource = PresentationSections,
                HasUnevenRows = true,
                GroupHeaderTemplate = new DataTemplate(() => new SectionHeaderViewCell(
                    nameof(VaultListPageModel.Section.Name), nameof(VaultListPageModel.Section.ItemCount),
                    new Thickness(16, Helpers.OnPlatform(20, 12, 12), 16, 12))),
                ItemTemplate = new DataTemplate(() => new VaultGroupingViewCell())
            };

            if(Device.RuntimePlatform == Device.iOS)
            {
                ListView.RowHeight = -1;
            }

            Search = new SearchBar
            {
                Placeholder = AppResources.SearchVault,
                FontSize = Device.GetNamedSize(NamedSize.Small, typeof(Button)),
                CancelButtonColor = Color.FromHex("3c8dbc")
            };
            // Bug with searchbar on android 7, ref https://bugzilla.xamarin.com/show_bug.cgi?id=43975
            if(Device.RuntimePlatform == Device.Android && _deviceInfoService.Version >= 24)
            {
                Search.HeightRequest = 50;
            }

            Title = AppResources.MyVault;

            ResultsStackLayout = new StackLayout
            {
                Children = { Search, ListView },
                Spacing = 0
            };

            var noDataLabel = new Label
            {
                Text = AppResources.NoItems,
                HorizontalTextAlignment = TextAlignment.Center,
                FontSize = Device.GetNamedSize(NamedSize.Small, typeof(Label)),
                Style = (Style)Application.Current.Resources["text-muted"]
            };

            NoDataStackLayout = new StackLayout
            {
                Children = { noDataLabel },
                VerticalOptions = LayoutOptions.CenterAndExpand,
                Padding = new Thickness(20, 0),
                Spacing = 20
            };

            var addCipherButton = new ExtendedButton
            {
                Text = AppResources.AddAnItem,
                Command = new Command(() => AddCipher()),
                Style = (Style)Application.Current.Resources["btn-primaryAccent"]
            };

            NoDataStackLayout.Children.Add(addCipherButton);

            LoadingIndicator = new ActivityIndicator
            {
                IsRunning = true,
                VerticalOptions = LayoutOptions.CenterAndExpand,
                HorizontalOptions = LayoutOptions.Center
            };

            Content = LoadingIndicator;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            MessagingCenter.Subscribe<ISyncService, bool>(_syncService, "SyncCompleted", (sender, success) =>
            {
                if(success)
                {
                    _filterResultsCancellationTokenSource = FetchAndLoadVault();
                }
            });

            ListView.ItemSelected += GroupingSelected;
            //Search.TextChanged += SearchBar_TextChanged;
            //Search.SearchButtonPressed += SearchBar_SearchButtonPressed;
            AddCipherItem?.InitEvents();

            _filterResultsCancellationTokenSource = FetchAndLoadVault();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            MessagingCenter.Unsubscribe<ISyncService, bool>(_syncService, "SyncCompleted");

            ListView.ItemSelected -= GroupingSelected;
            //Search.TextChanged -= SearchBar_TextChanged;
            //Search.SearchButtonPressed -= SearchBar_SearchButtonPressed;
            AddCipherItem?.Dispose();
        }

        private void AdjustContent()
        {
            if(PresentationSections.Count > 0 || !string.IsNullOrWhiteSpace(Search.Text))
            {
                Content = ResultsStackLayout;
            }
            else
            {
                Content = NoDataStackLayout;
            }
        }

        private CancellationTokenSource FetchAndLoadVault()
        {
            var cts = new CancellationTokenSource();
            _filterResultsCancellationTokenSource?.Cancel();

            Task.Run(async () =>
            {
                var sections = new List<VaultListPageModel.Section>();
                var ciphers = await _cipherService.GetAllAsync();
                var collectionsDict = (await _collectionService.GetAllCipherAssociationsAsync())
                    .GroupBy(c => c.Item2).ToDictionary(g => g.Key, v => v.ToList());

                var folderCounts = new Dictionary<string, int> { ["none"] = 0 };
                foreach(var cipher in ciphers)
                {
                    if(cipher.FolderId != null)
                    {
                        if(!folderCounts.ContainsKey(cipher.FolderId))
                        {
                            folderCounts.Add(cipher.FolderId, 0);
                        }
                        folderCounts[cipher.FolderId]++;
                    }
                    else
                    {
                        folderCounts["none"]++;
                    }
                }

                var folders = await _folderService.GetAllAsync();
                var folderGroupings = folders?
                    .Select(f => new VaultListPageModel.Grouping(f, folderCounts.ContainsKey(f.Id) ? folderCounts[f.Id] : 0))
                    .OrderBy(g => g.Name).ToList();
                folderGroupings.Add(new VaultListPageModel.Grouping(AppResources.FolderNone, folderCounts["none"]));
                if(folderGroupings?.Any() ?? false)
                {
                    sections.Add(new VaultListPageModel.Section(folderGroupings, AppResources.Folders));
                }

                var collections = await _collectionService.GetAllAsync();
                var collectionGroupings = collections?
                    .Select(c => new VaultListPageModel.Grouping(c,
                        collectionsDict.ContainsKey(c.Id) ? collectionsDict[c.Id].Count() : 0))
                   .OrderBy(g => g.Name).ToList();
                if(collectionGroupings?.Any() ?? false)
                {
                    sections.Add(new VaultListPageModel.Section(collectionGroupings, AppResources.Collections));
                }

                Device.BeginInvokeOnMainThread(() =>
                {
                    if(sections.Any())
                    {
                        PresentationSections.ResetWithRange(sections);
                    }

                    AdjustContent();
                });
            }, cts.Token);

            return cts;
        }

        private void GroupingSelected(object sender, SelectedItemChangedEventArgs e)
        {
            var grouping = e.SelectedItem as VaultListPageModel.Grouping;
            if(grouping == null)
            {
                return;
            }

            ((ListView)sender).SelectedItem = null;
        }

        private async void AddCipher()
        {
            var type = await _userDialogs.ActionSheetAsync(AppResources.SelectTypeAdd, AppResources.Cancel, null, null,
                AppResources.TypeLogin, AppResources.TypeCard, AppResources.TypeIdentity, AppResources.TypeSecureNote);

            var selectedType = CipherType.SecureNote;
            if(type == AppResources.Cancel)
            {
                return;
            }
            else if(type == AppResources.TypeLogin)
            {
                selectedType = CipherType.Login;
            }
            else if(type == AppResources.TypeCard)
            {
                selectedType = CipherType.Card;
            }
            else if(type == AppResources.TypeIdentity)
            {
                selectedType = CipherType.Identity;
            }

            var page = new VaultAddCipherPage(selectedType);
            await Navigation.PushForDeviceAsync(page);
        }

        private class AddCipherToolBarItem : ExtendedToolbarItem
        {
            private readonly VaultListGroupingsPage _page;

            public AddCipherToolBarItem(VaultListGroupingsPage page)
                : base(() => page.AddCipher())
            {
                _page = page;
                Text = AppResources.Add;
                Icon = "plus.png";
            }
        }
    }
}