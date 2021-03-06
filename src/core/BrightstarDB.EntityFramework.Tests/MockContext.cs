﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using BrightstarDB.Client;
using BrightstarDB.EntityFramework.Query;
using BrightstarDB.EntityFramework.Tests.ContextObjects;

namespace BrightstarDB.EntityFramework.Tests
{
    /// <summary>
    /// A mock context that supports recording the last SPARQL query that was exectued.
    /// </summary>
    public class MockContext : EntityContext
    {
        private string _lastQuery;
        private SparqlQueryContext _lastQueryContext;
        public string LastSparqlQuery { get { return _lastQuery; } }
        public SparqlQueryContext LastSparqlQueryContext { get { return _lastQueryContext; } }

        public MockContext() : base()
        {
        }

        public MockContext(EntityMappingStore mappingStore) : base(mappingStore)
        {
            mappingStore.AddImplMapping<IDinner,Dinner>();
            mappingStore.AddImplMapping<ContextObjects.ICompany, ContextObjects.Company>();
            mappingStore.AddImplMapping<ContextObjects.IMarket,ContextObjects.Market>();
            mappingStore.AddImplMapping<ContextObjects.IPerson,ContextObjects.Person>();
            mappingStore.AddImplMapping<IRsvp,Rsvp>();
            mappingStore.AddImplMapping<IConcept, Concept>();
        }

        #region Overrides of LdoContext

        public override void SaveChanges()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Updates a single object in the object context with data from the data source
        /// </summary>
        /// <param name="mode">A <see cref="RefreshMode"/> value that indicates whether property changes
        /// in the object context are overwritten with property changes from the data source</param>
        /// <param name="entity">The object to be refreshed</param>
        public override void Refresh(RefreshMode mode, object entity)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Update a collection of objects in the object context with data from the data source
        /// </summary>
        /// <param name="mode">A <see cref="RefreshMode"/> value that indicates whether property changes
        /// in the object context are overwritten with property changes from the data source</param>
        /// <param name="entities">The objects to be refreshed</param>
        public override void Refresh(RefreshMode mode, IEnumerable entities)
        {
            throw new NotImplementedException();
        }

        public override XDocument ExecuteQuery(string sparqlQuery)
        {
            _lastQuery = sparqlQuery;
            return new XDocument();
        }

        public override IEnumerable<T> ExecuteQuery<T>(SparqlQueryContext sparqlQuery)
        {
            _lastQuery = sparqlQuery.SparqlQuery;
            _lastQueryContext = sparqlQuery;
            yield break;
        }

        public override IEnumerable<T> ExecuteInstanceQuery<T>(string instanceIdentifier, string typeIdentifier)
        {
            _lastQuery = String.Format("ASK {{ <{0}> a <{1}>. }}", instanceIdentifier, typeIdentifier);
            _lastQueryContext = null;
            yield break;
        }

        public override string MapIdToUri(PropertyInfo propertyInfo, string id)
        {
            return ("id:" + id);
        }

        public override void DeleteObject(object o)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Return the RDF datatype to apply to literals of the specified system type.
        /// </summary>
        /// <param name="systemType"></param>
        /// <returns></returns>
        public override string GetDatatype(Type systemType)
        {
            if (typeof(Int32).Equals(systemType))
            {
                return Integer;
            }
            if (typeof(Decimal).Equals(systemType))
            {
                return Decimal;
            }
            if (typeof(Double).Equals(systemType))
            {
                return Double;
            }
            return null;
        }

        public override IList<string> GetDataset()
        {
            return null;
        }

        protected override void Cleanup()
        {
            // Nothing to clean up
        }

        #endregion

        public IQueryable<IDinner> Dinners { get { return new MockLdoSet<IDinner>(this); } }
        public IQueryable<IRsvp> Rsvps { get { return new MockLdoSet<IRsvp>(this); } }
        public IQueryable<ContextObjects.IMarket> Markets { get { return new MockLdoSet<ContextObjects.IMarket>(this); } }
        public IQueryable<ContextObjects.ICompany> Companies { get { return new MockLdoSet<ContextObjects.ICompany>(this); } }
        public IQueryable<ContextObjects.IPerson> People { get { return new MockLdoSet<ContextObjects.IPerson>(this); } }
        public IQueryable<IConcept> Concepts { get { return new MockLdoSet<IConcept>(this); } }

        /// <summary>
        /// The XML namespace for W3C XML Schema
        /// </summary>
        public const string XsdNamespace = "http://www.w3.org/2001/XMLSchema#";
        /// <summary>
        /// The XML Schema integer datatype URI
        /// </summary>
        public const string Integer = XsdNamespace + "integer";
        /// <summary>
        /// The XML Schema decimal datatype URI
        /// </summary>
        public const string Decimal = XsdNamespace + "decimal";

        public const string Double = XsdNamespace + "double";

    }
}
