﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Web.Http;
using AutoMapper;
using CodeSmith.Core.Helpers;
using Exceptionless.Core;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Web;
using Exceptionless.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;

namespace Exceptionless.Api.Controllers {
    public abstract class RepositoryApiController<TRepository, TModel, TViewModel, TNewModel, TUpdateModel> : ExceptionlessApiController
            where TRepository : MongoRepositoryWithIdentity<TModel>
            where TModel : class, IIdentity, new()
            where TViewModel : class, IIdentity, new()
            where TNewModel : class, new()
            where TUpdateModel : class, new() {
        protected readonly TRepository _repository;
        protected static readonly bool _isOwnedByOrganization;

        static RepositoryApiController() {
            _isOwnedByOrganization = typeof(IOwnedByOrganization).IsAssignableFrom(typeof(TModel));
        }

        public RepositoryApiController(TRepository repository) {
            _repository = repository;

            Run.Once(CreateMaps);
        }

        protected virtual void CreateMaps() {
            if (Mapper.FindTypeMapFor<TModel, TViewModel>() == null)
                Mapper.CreateMap<TModel, TViewModel>();
            if (Mapper.FindTypeMapFor<TNewModel, TModel>() == null)
                Mapper.CreateMap<TNewModel, TModel>();
        }

        #region Get

        public virtual IHttpActionResult Get(string organization = null, string before = null, string after = null, int limit = 10) {
            IMongoQuery query = null;
            if (_isOwnedByOrganization && !String.IsNullOrEmpty(organization))
                query = Query.EQ("oid", ObjectId.Parse(organization));

            var options = new GetEntitiesOptions { Query = query, AfterValue = after, BeforeValue = before, Limit = limit };
            var results = GetEntities<TViewModel>(options);
            return OkWithResourceLinks(results, options.HasMore);
        }

        protected List<T> GetEntities<T>(GetEntitiesOptions options) {
            options.Limit = GetLimit(options.Limit);

            // filter by the associated organizations
            if (_isOwnedByOrganization)
                options.Query = options.Query.And(Query.In(CommonFieldNames.OrganizationId, GetAssociatedOrganizationIds().Select(id => new BsonObjectId(new ObjectId(id)))));

            if (!String.IsNullOrEmpty(options.BeforeValue) && options.BeforeQuery == null)
                options.BeforeQuery = Query.LT("_id", ObjectId.Parse(options.BeforeValue));

            if (!String.IsNullOrEmpty(options.AfterValue) && options.AfterQuery == null)
                options.AfterQuery = Query.LT("_id", ObjectId.Parse(options.AfterValue));

            options.Query = options.Query.And(options.BeforeQuery);
            options.Query = options.Query.And(options.AfterQuery);

            var cursor = _repository.Collection.Find(options.Query ?? Query.Null).SetLimit(options.Limit + 1);
            if (options.Fields != null)
                cursor.SetFields(options.Fields);
            if (options.SortBy != null)
                cursor.SetSortOrder(options.SortBy);

            var result = cursor.ToList();
            options.HasMore = result.Count > options.Limit;

            if (typeof(T) == typeof(TModel))
                return result.Take(options.Limit).Cast<T>().ToList();

            return result.Take(options.Limit).Select(Mapper.Map<TModel, T>).ToList();
        }
        
        public virtual IHttpActionResult GetById(string id) {
            TModel model = GetModel(id);
            if (model == null)
                return NotFound();

            if (typeof(TViewModel) == typeof(TModel))
                return Ok(model);

            return Ok(Mapper.Map<TModel, TViewModel>(model));
        }

        protected virtual TModel GetModel(string id) {
            if (String.IsNullOrEmpty(id))
                return null;

            var model = _repository.GetByIdCached(id);
            if (_isOwnedByOrganization && model != null && !IsInOrganization(((IOwnedByOrganization)model).OrganizationId))
                return null;

            return model;
        }

        #endregion

        #region Post

        public virtual IHttpActionResult Post(TNewModel value) {
            if (value == null)
                return BadRequest();

            var orgModel = value as IOwnedByOrganization;
            // if no organization id is specified, default to the user's 1st associated org.
            if (orgModel != null && String.IsNullOrEmpty(orgModel.OrganizationId) && GetAssociatedOrganizationIds().Any())
                orgModel.OrganizationId = GetDefaultOrganizationId();

            var mapped = Mapper.Map<TNewModel, TModel>(value);
            var permission = CanAdd(mapped);
            if (!permission.Allowed)
                return permission.HttpActionResult ?? BadRequest();

            TModel model;
            try {
                model = AddModel(mapped);
            } catch (WriteConcernException) {
                return Conflict();
            }

            var viewModel = Mapper.Map<TModel, TViewModel>(model);
            return Created(GetEntityLink(model.Id), viewModel);
        }

        protected virtual Uri GetEntityLink(string id) {
            return new Uri(Url.Link(String.Format("Get{0}ById", typeof(TModel).Name), new { id }));
        }

        protected virtual PermissionResult CanAdd(TModel value) {
            var orgModel = value as IOwnedByOrganization;
            if (orgModel == null)
                return PermissionResult.Allow;

            if (!IsInOrganization(orgModel.OrganizationId))
                return PermissionResult.DenyWithResult(BadRequest("Invalid organization id specified."));

            return PermissionResult.Allow;
        }

        protected virtual TModel AddModel(TModel value) {
            return _repository.Add(value);
        }

        #endregion

        #region Patch

        public virtual IHttpActionResult Patch(string id, Delta<TUpdateModel> changes) {
            // if there are no changes in the delta, then ignore the request
            if (changes == null || !changes.GetChangedPropertyNames().Any())
                return Ok();
            
            TModel original = GetModel(id);
            if (original == null)
                return NotFound();

            var permission = CanUpdate(original, changes);
            if (!permission.Allowed)
                return permission.HttpActionResult ?? BadRequest();

            UpdateModel(original, changes);

            return Ok();
        }

        protected virtual PermissionResult CanUpdate(TModel original, Delta<TUpdateModel> changes) {
            var orgModel = original as IOwnedByOrganization;
            if (orgModel != null && !IsInOrganization(orgModel.OrganizationId))
                return PermissionResult.DenyWithResult(BadRequest("Invalid organization id specified."));

            return PermissionResult.Allow;
        }

        protected virtual TModel UpdateModel(TModel original, Delta<TUpdateModel> changes) {
            changes.Patch(original);
            return _repository.Update(original);
        }

        #endregion

        #region Delete

        public virtual IHttpActionResult Delete(string id) {
            TModel item = GetModel(id);
            if (item == null)
                return BadRequest();

            var permission = CanDelete(item);
            if (!permission.Allowed)
                return permission.HttpActionResult ?? NotFound();

            DeleteModel(item);
            return StatusCode(HttpStatusCode.NoContent);
        }

        protected virtual PermissionResult CanDelete(TModel value) {
            var orgModel = value as IOwnedByOrganization;
            if (orgModel != null && !IsInOrganization(orgModel.OrganizationId))
                return PermissionResult.DenyWithResult(NotFound());

            return PermissionResult.Allow;
        }

        protected virtual void DeleteModel(TModel value) {
            _repository.Delete(value);
        }

        #endregion
    }

    public class GetEntitiesOptions {
        public bool HasMore { get; set; }
        public IMongoQuery Query { get; set; }
        public IMongoFields Fields { get; set; }
        public IMongoSortBy SortBy { get; set; }
        public string BeforeValue { get; set; }
        public IMongoQuery BeforeQuery { get; set; }
        public string AfterValue { get; set; }
        public IMongoQuery AfterQuery { get; set; }
        public int Limit { get; set; }
    }
}