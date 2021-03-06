﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BrightstarDB.Client;
using BrightstarDB.Dto;
using BrightstarDB.Polaris.Messages;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using VDS.RDF.Parsing;

namespace BrightstarDB.Polaris.ViewModel
{
    public class Connection : ViewModelBase
    {
        private static readonly ConnectionType[] ConnectionTypesArray = new[]
                                                                            {
                                                                                ConnectionType.Embedded,
                                                                                ConnectionType.Rest
                                                                            };

        private ConnectionString _connectionString;
        private ConnectionType _connectionType;
        private string _directoryPath;
        private bool _isValid;
        private string _name;
        private string _serverEndpoint;

        public Connection(string name, string connectionString)
        {
            _name = name;
            _connectionString = new ConnectionString(connectionString);
            ParseConnectionString();
            Initialize();
        }

        public Connection()
        {
            Initialize();
        }

        public String Name
        {
            get { return _name; }
            set { _name = value; Validate(); RaisePropertyChanged("Name"); }
        }

        public IEnumerable<ConnectionType> ConnectionTypes
        {
            get { return ConnectionTypesArray; }
        }

        public bool IsValid
        {
            get { return _isValid; }
            private set { _isValid = value; RaisePropertyChanged("IsValid"); }
        }

        public ConnectionString ConnectionString
        {
            get { return _connectionString; }
            set
            {
                _connectionString = value;
                ParseConnectionString();
                RaisePropertyChanged("ConnectionString");
            }
        }

        public bool HasConnectionError { get { return !String.IsNullOrEmpty(ConnectionError); } }

        private string _connectionError;

        public string ConnectionError
        {
            get { return _connectionError; }
            private set
            {
                _connectionError = value;
                RaisePropertyChanged("ConnectionError");
                RaisePropertyChanged("HasConnectionError");
            }
        }

        public RelayCommand SaveChangesCommand { get; private set; }

        public ObservableCollection<Store> Stores { get; set; }

        public ConnectionType ConnectionType
        {
            get { return _connectionType; }
            set
            {
                _connectionType = value;
                Validate();
                RaisePropertyChanged("ConnectionType");
            }
        }

        public string ServerEndpoint
        {
            get { return _serverEndpoint; }
            set
            {
                _serverEndpoint = value;
                Validate();
                RaisePropertyChanged("ServerEndpoint");
            }
        }

        public string DirectoryPath
        {
            get { return _directoryPath; }
            set
            {
                _directoryPath = value;
                Validate();
                RaisePropertyChanged("DirectoryPath");
            }
        }

        public ObservableCollection<String> ValidationMessages { get; set; }

        private void Initialize()
        {
            Stores = new ObservableCollection<Store>();
            ValidationMessages = new ObservableCollection<string>();
            SaveChangesCommand = new RelayCommand(SaveChanges);
            Validate();
        }

        private void SaveChanges()
        {
            var connString = MakeConnectionString();
            _connectionString = new ConnectionString(connString);
            TryConnect();
            Messenger.Default.Send(new CloseWindowMessage {Name = "ConnectionPropertiesDialog", DialogResult = true});
        }

        private string MakeConnectionString()
        {
            if (ConnectionType == ConnectionType.Embedded)
            {
                return String.Format("type=embedded;storesDirectory={0}", DirectoryPath);
            }
            if (ConnectionType == ConnectionType.Rest)
            {
                var connString = String.Format("type=rest;endpoint={0}", ServerEndpoint);
                return connString;
            }
            throw new NotSupportedException(String.Format("Cannot generate connection string for connection type {0}", ConnectionType));
        }

        private void ParseConnectionString()
        {
            _connectionType = _connectionString.Type;
            switch (ConnectionString.Type)
            {
                case ConnectionType.Embedded:
                    _serverEndpoint = null;
                    _directoryPath = _connectionString.StoresDirectory;
                    break;
                case ConnectionType.Rest:
                    _serverEndpoint = _connectionString.ServiceEndpoint;
                    _directoryPath = null;
                    break;
                default:
                    throw new NotSupportedException(String.Format("Cannot parse connection string containing obsolete connection type property '{0}'", _connectionString.Type));
            }
        }

        public void Validate()
        {
            ValidationMessages.Clear();
            if (String.IsNullOrEmpty(Name))
            {
                ValidationMessages.Add("A value is required for the Connection Name.");
            }
            switch (ConnectionType)
            {
                case ConnectionType.Embedded:
                    if (String.IsNullOrEmpty(DirectoryPath))
                    {
                        ValidationMessages.Add("A value is required for the Stores Directory path.");
                    }
                    else if (!Directory.Exists(DirectoryPath))
                    {
                        ValidationMessages.Add(String.Format("Cannot find the directory {0}", DirectoryPath));
                    }
                    break;
                case ConnectionType.Rest:
                    if (String.IsNullOrEmpty(ServerEndpoint))
                    {
                        ValidationMessages.Add("A value is required for the Server Address.");
                    }
                    else
                    {
                        Uri parsedUri;
                        if (!Uri.TryCreate(ServerEndpoint, UriKind.Absolute, out parsedUri))
                        {
                            ValidationMessages.Add("The Server Address must be a valid HTTP/HTTPS URL");
                        }
                        else
                        {
                            var scheme = parsedUri.Scheme.ToLowerInvariant();
                            if (!(scheme.Equals("http") || scheme.Equals("https")))
                            {
                                ValidationMessages.Add("The Server Address must be a valid HTTP/HTTPS URL");
                            }
                        }
                    }
                    break;
            }
            IsValid = ValidationMessages.Count == 0;
        }

        public void TryConnect()
        {
            try
            {
                Stores.Clear();
                ConnectionError = "Attempting to connect";
                var client = BrightstarService.GetClient(_connectionString);
                foreach(var storeName in  client.ListStores())
                {
                    Stores.Add(new Store(this, storeName));
                }
                ConnectionError = null;
            }
            catch (Exception)
            {
                ConnectionError = "Could not establish connection";
            }
        }

        public void CreateStore(Store store)
        {
            var client = BrightstarService.GetClient(_connectionString);
            client.CreateStore(store.Location);
            Stores.Add(store);
        }

        public void DeleteStore(string storeName)
        {
            var client = BrightstarService.GetClient(_connectionString);
            client.DeleteStore(storeName);
            foreach(var item in Stores.Where(s=>s.Location.Equals(storeName)).ToList())
            {
                Stores.Remove(item);
            }
        }

        public XDocument ExecuteQuery(Store store, string sparqlQueryString, CommitPointViewModel targetCommitPoint)
        {
            var client = BrightstarService.GetClient(_connectionString);
            try
            {
                if (targetCommitPoint == null)
                {
                    using (var resultsStream = client.ExecuteQuery(store.Location, sparqlQueryString))
                    {
                        XDocument result = XDocument.Load(resultsStream);
                        return result;
                    }
                }
                var commitPoint = client.GetCommitPoint(store.Location, targetCommitPoint.CommitTime);
                if (commitPoint == null)
                {
                    throw new Exception("Could not retrieve specified commit point from store.");
                }
                using(var resultsStream = client.ExecuteQuery(commitPoint, sparqlQueryString))
                {
                    XDocument result = XDocument.Load(resultsStream);
                    return result;
                }
            }
            catch (BrightstarClientException brightstarClientException)
            {
                ExtractSyntaxError(brightstarClientException.InnerException);
                throw new SparqlQueryException(brightstarClientException);
            }
            catch (Exception ex)
            {
                throw new SparqlQueryException(ex);
            }
        }

        public void ExecuteUpdate(Store store, string sparqlUpdateString)
        {
            try
            {
                var client = BrightstarService.GetClient(_connectionString);
                var jobInfo = client.ExecuteUpdate(store.Location, sparqlUpdateString);
                if (!jobInfo.JobCompletedOk)
                {
                    if (jobInfo.ExceptionInfo != null)
                    {
                        ExtractSyntaxError(jobInfo.ExceptionInfo);
                        throw new SparqlUpdateException(jobInfo.ExceptionInfo);
                    }
                    throw new SparqlUpdateException(jobInfo.StatusMessage);
                }
            }
            catch (Exception ex)
            {
                throw new SparqlUpdateException(ex);
            }
        }

        private static void ExtractSyntaxError(ExceptionDetailObject exceptionDetail)
        {
            if (exceptionDetail == null) return;
            if (exceptionDetail.Type.Equals("VDS.RDF.Parsing.RdfParseException"))
            {
                throw new RdfParseException(exceptionDetail.Message);
            }
            ExtractSyntaxError(exceptionDetail.InnerException);
        }

        private static void ExtractSyntaxError(Exception exception)
        {
            if (exception == null) return;
            if (exception is RdfParseException)
            {
                throw exception;
            }
            ExtractSyntaxError(exception);
        }

        public IEnumerable<CommitPointViewModel> GetCommitPoints(Store store, int skip, int take)
        {
            var client = BrightstarService.GetClient(ConnectionString);
            return client.GetCommitPoints(store.Location, skip, take).Select(x => new CommitPointViewModel(x));
        }

        public void RevertToCommitPoint(Store store, CommitPointViewModel targetCommitPoint)
        {
            if (targetCommitPoint == null) return;
            var client = BrightstarService.GetClient(ConnectionString);
            var commitPoint = client.GetCommitPoint(store.Location, targetCommitPoint.CommitTime);
            if (commitPoint == null)
            {
                throw new ApplicationException(String.Format("Could not find commit point for {0} (ID: {1})", targetCommitPoint.CommitTime, targetCommitPoint.Id));
            }
            client.RevertToCommitPoint(store.Location, commitPoint);
        }

        public IEnumerable<CommitPointViewModel> GetCommitPoints(Store store, DateTime latest, DateTime earliest, int skip, int take)
        {
            var client = BrightstarService.GetClient(ConnectionString);
            return
                client.GetCommitPoints(store.Location, latest, earliest, skip, take).Select(
                    x => new CommitPointViewModel(x));
        }

        public StatisticsViewModel GetStatistics(Store store)
        {
            var client = BrightstarService.GetClient(ConnectionString);
            var stats = client.GetStatistics(store.Location);
            return stats == null ? null : new StatisticsViewModel(stats);
        }

        public Connection Clone()
        {
            var ret = new Connection(Name, ConnectionString.ToString());
            ret.ParseConnectionString();
            return ret;
        }
    }
}