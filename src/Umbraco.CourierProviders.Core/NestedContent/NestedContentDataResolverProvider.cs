namespace DisPlay.Umbraco.CourierProviders.Core.NestedContent
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using global::Umbraco.Courier.Core;
    using global::Umbraco.Courier.Core.Logging;
    using global::Umbraco.Courier.Core.ProviderModel;
    using global::Umbraco.Courier.DataResolvers;
    using global::Umbraco.Courier.ItemProviders;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public class NestedContentDataResolverProvider : PropertyDataResolverProvider
    {
        private enum Direction
        {
            Extracting,
            Packaging
        }

        public override string EditorAlias
        {
            get { return "Our.Umbraco.NestedContent"; }
        }

        public override void PackagingDataType(DataType item)
        {
            var json = item.Prevalues.FirstOrDefault(_ => _.Alias == "contentTypes");
            if (json == null)
                return;

            var contentTypes = JsonConvert.DeserializeObject<JArray>(json.Value);
            foreach (dynamic contentTypeObject in contentTypes)
            {
                if (contentTypeObject.ncAlias == null)
                    continue;

                var alias = contentTypeObject.ncAlias.ToString();
                var contentType =
                    ExecutionContext.DatabasePersistence.RetrieveItem<DocumentType>(new ItemIdentifier(alias,
                        ItemProviderIds.documentTypeItemProviderGuid));

                if (contentType != null)
                    item.Dependencies.Add(contentType.UniqueId.ToString(), ItemProviderIds.documentTypeItemProviderGuid);
            }
        }

        public override void ExtractingProperty(Item item, ContentProperty propertyData)
        {
            ProcessProperty(item, propertyData, Direction.Extracting);
        }

        public override void PackagingProperty(Item item, ContentProperty propertyData)
        {
            ProcessProperty(item, propertyData, Direction.Packaging);
        }

        private void ProcessProperty(Item item, ContentProperty propertyData, Direction direction)
        {
            var propertyItemProvider = ItemProviderCollection.Instance.GetProvider(ItemProviderIds.propertyDataItemProviderGuid, ExecutionContext);

            if (direction == Direction.Packaging)
                item.Dependencies.Add(propertyData.DataType.ToString(), ItemProviderIds.dataTypeItemProviderGuid);

            var array = JsonConvert.DeserializeObject<JArray>(propertyData.Value.ToString());
            foreach (var ncObj in array)
            {
                var doctypeAlias = ncObj["ncContentTypeAlias"].ToString();
                var doctype =
                    ExecutionContext.DatabasePersistence.RetrieveItem<DocumentType>(new ItemIdentifier(doctypeAlias,
                        ItemProviderIds.documentTypeItemProviderGuid));
                if (doctype == null)
                    continue;

                foreach (var propertyType in doctype.Properties)
                {
                    object o;
                    if ((o = ncObj[propertyType.Alias]) != null)
                    {
                        //make fake item
                        var value = o.ToString();
                        var datatype =
                            ExecutionContext.DatabasePersistence.RetrieveItem<DataType>(
                                new ItemIdentifier(propertyType.DataTypeDefinitionId.ToString(),
                                    ItemProviderIds.dataTypeItemProviderGuid));

                        var fakeItem = new ContentPropertyData
                        {
                            ItemId = item.ItemId,
                            Name = string.Format("{0} [{1}: Nested {2} ({3})]", item.Name, EditorAlias, datatype.PropertyEditorAlias, propertyType.Alias),
                            Data = new List<ContentProperty>
                            {
                                new ContentProperty
                                {
                                    Alias = propertyType.Alias,
                                    DataType = datatype.UniqueID,
                                    PropertyEditorAlias = datatype.PropertyEditorAlias,
                                    Value = value
                                }
                            }
                        };

                        if (direction == Direction.Packaging)
                        {
                            try
                            {
                                // run the 'fake' item through Courier's data resolvers
                                ResolutionManager.Instance.PackagingItem(fakeItem, propertyItemProvider);
                            }
                            catch (Exception ex)
                            {
                                CourierLogHelper.Error<NestedContentDataResolverProvider>(
                                    string.Concat("Error packaging data value: ", fakeItem.Name), ex);
                            }
                        }
                        else if (direction == Direction.Extracting)
                        {
                            try
                            {
                                // run the 'fake' item through Courier's data resolvers
                                ResolutionManager.Instance.ExtractingItem(fakeItem, propertyItemProvider);
                            }
                            catch (Exception ex)
                            {
                                CourierLogHelper.Error<NestedContentDataResolverProvider>(
                                    string.Concat("Error extracting data value: ", fakeItem.Name), ex);
                            }
                        }

                        // pass up the dependencies and resources
                        item.Dependencies.AddRange(fakeItem.Dependencies);
                        item.Resources.AddRange(fakeItem.Resources);

                        if (fakeItem.Data != null && fakeItem.Data.Any())
                        {
                            var firstDataType = fakeItem.Data.FirstOrDefault();
                            if (firstDataType != null)
                            {
                                // set the resolved property data value
                                string serializedValue = firstDataType.Value as string ??
                                                         JsonConvert.SerializeObject(firstDataType.Value);

                                try
                                {
                                    // attempt to parse in case of valid json object
                                    ncObj[propertyType.Alias] = JToken.Parse(serializedValue);
                                }
                                catch(JsonReaderException ex)
                                {
                                    // revert to JValue
                                    ncObj[propertyType.Alias] = new JValue(serializedValue);
                                }

                                // (if packaging) add a dependency for the property's data-type
                                if (direction == Direction.Packaging)
                                    item.Dependencies.Add(firstDataType.DataType.ToString(), ItemProviderIds.dataTypeItemProviderGuid);
                            }
                        }
                    }
                }
            }

            propertyData.Value = JsonConvert.SerializeObject(array);
        }
    }
}