﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Helper class that resolves dependencies in the Unity scene.
/// </summary>
public class DependencyResolver
{
    /// <summary>
    /// Enumerate the scene and find objects of interest to the dependency resolver.
    /// 
    /// WARNING: This function can be expensive. Call it only once and cache the result if you need it.
    /// </summary>
    public void FindObjects(IEnumerable<GameObject> allGameObjects, List<MonoBehaviour> injectables, List<MonoBehaviour> globalServices)
    {
        foreach (var gameObject in allGameObjects)
        {
            foreach (var component in gameObject.GetComponents<MonoBehaviour>())
            {
                var componentType = component.GetType();
                var hasInjectableProperties = componentType.GetProperties()
                    .Where(IsPropertyInjectable)
                    .Any();
                if (hasInjectableProperties)
                {
                    injectables.Add(component);
                }

                var hasServiceAttribute = componentType.GetCustomAttributes(true)
                    .Where(attribute => attribute is ServiceAttribute)
                    .Any();
                if (hasServiceAttribute)
                {
                    globalServices.Add(component);
                }
            }
        }
    }

    /// <summary>
    /// Determine if a property requires dependency resolution, checks if the property has the 'Inject' attribute.
    /// </summary>
    private bool IsPropertyInjectable(PropertyInfo property)
    {
        return property.GetCustomAttributes(true)
            .Where(attribute => attribute is InjectAttribute)
            .Count() > 0;
    }

    /// <summary>
    /// Use C# reflection to find all properties of an object that require dependency resolution and injection.
    /// </summary>
    private IEnumerable<IInjectableMember> FindInjectableProperties(object injectable)
    {
        var type = injectable.GetType();
        return type.GetProperties()
            .Where(IsPropertyInjectable)
            .Select(property => new InjectableProperty(property))
            .Cast<IInjectableMember>();
    }

    /// <summary>
    /// Get the ancestors from a particular Game Object. This means the parent object, the grand parent and so on up to the root of the hierarchy.
    /// </summary>
    private IEnumerable<GameObject> GetAncestors(GameObject fromGameObject)
    {
        for (var parent = fromGameObject.transform.parent; parent != null; parent = parent.parent)
        {
            yield return parent.gameObject; // Mmmmm... LINQ.
        }
    }

    /// <summary>
    /// Walk up the hierarchy (towards the root) and find an injectable dependency that matches the specified type.
    /// </summary>
    private MonoBehaviour FindDependencyInHierarchy(Type injectionType, GameObject fromGameObject)
    {
        foreach (var ancestor in GetAncestors(fromGameObject))
        {
            foreach (var component in ancestor.GetComponents<MonoBehaviour>())
            {
                if (injectionType.IsAssignableFrom(component.GetType()))
                {
                    return component;
                }
            }
        }

        return null;
    }

    public interface IInjectableMember
    {
        /// <summary>
        /// The one thing we want to do is set the value of the member.
        /// </summary>
        void SetValue(object owner, object value);

        /// <summary>
        /// Get the name  of the member.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Get the type of the member.
        /// </summary>
        Type MemberType { get; }
    }

    public class InjectableProperty : IInjectableMember
    {
        private PropertyInfo propertyInfo;

        public InjectableProperty(PropertyInfo propertyInfo)
        {
            this.propertyInfo = propertyInfo;
        }

        /// <summary>
        /// The one thing we want to do is set the value of the member.
        /// </summary>
        public void SetValue(object owner, object value)
        {
            propertyInfo.SetValue(owner, value, null);
        }

        /// <summary>
        /// Get the name  of the member.
        /// </summary>
        public string Name
        {
            get
            {
                return propertyInfo.Name;
            }
        }

        /// <summary>
        /// Get the type of the member.
        /// </summary>
        public Type MemberType
        {
            get
            {
                return propertyInfo.PropertyType;
            }            
        }
    }

    /// <summary>
    /// Attempt to resolve a member dependency by scanning up the hiearchy for a MonoBehaviour that mathces the injection type.
    /// </summary>
    private bool ResolveMemberDependencyFromHierarchy(MonoBehaviour injectable, IInjectableMember injectableMember)
    {
        // Find a match in the hierarchy.
        var toInject = FindDependencyInHierarchy(injectableMember.MemberType, injectable.gameObject);
        if (toInject != null)
        {
            try
            {
                Debug.Log("Injecting " + toInject.GetType().Name + " from hierarchy (GameObject: '" + toInject.gameObject.name + "') into " + injectable.GetType().Name + " at member " + injectableMember.Name + " on GameObject '" + injectable.name + "'.", injectable);

                injectableMember.SetValue(injectable, toInject);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, injectable); // Bad type??!
            }

            return true;
        }
        else
        {
            // Failed to find a match.
            return false;
        }
    }

    /// <summary>
    /// Find a service that matches the specified type that is to be injected.
    /// </summary>
    private MonoBehaviour FindResolveableService(Type injectionType, IEnumerable<MonoBehaviour> globalServices)
    {
        foreach (var service in globalServices)
        {
            if (injectionType.IsAssignableFrom(service.GetType()))
            {
                return service;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempt to resolve a member dependency from global services.
    /// Returns false is no such dependency was found.
    /// </summary>
    private bool ResolveMemberDependencyFromService(MonoBehaviour injectable, IInjectableMember injectableMember, IEnumerable<MonoBehaviour> globalServices)
    {
        // Find a match in the list of global services.
        var toInject = FindResolveableService(injectableMember.MemberType, globalServices);
        if (toInject != null)
        {
            try
            {
                Debug.Log("Injecting global service " + toInject.GetType().Name + " (GameObject: '" + toInject.gameObject.name + "') into " + injectable.GetType().Name + " at member " + injectableMember.Name + " on GameObject '" + injectable.name + "'.", injectable);

                injectableMember.SetValue(injectable, toInject);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, injectable);
            }

            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve a member dependency and inject the resolved valued.
    /// </summary>
    private void ResolveMemberDependency(MonoBehaviour injectable, IInjectableMember injectableMember, IEnumerable<MonoBehaviour> globalServices)
    {
        if (!ResolveMemberDependencyFromHierarchy(injectable, injectableMember))
        {
            if (!ResolveMemberDependencyFromService(injectable, injectableMember, globalServices))
            {
                Debug.LogError(
                    "Failed to resolve dependency for member. Member: " + injectableMember.Name + ", MonoBehaviour: " + injectable.GetType().Name + ", GameObject: " + injectable.gameObject.name + "\r\n" +
                    "Failed to find a dependency that matches " + injectableMember.MemberType.Name + ".",
                    injectable
                );
            }
        }
    }

    /// <summary>
    /// Resolve dependenies for an 'injectable' object.
    /// </summary>
    private void ResolveDependencies(MonoBehaviour injectable, IEnumerable<MonoBehaviour> globalServices)
    {
        var injectableProperties = FindInjectableProperties(injectable);
        foreach (var injectableMember in injectableProperties)
        {
            ResolveMemberDependency(injectable, injectableMember, globalServices);
        }
    }

    /// <summary>
    /// Resolve all depenencies in the entire scene.
    /// </summary>
    public void ResolveScene()
    {
        var allGameObjects = GameObject.FindObjectsOfType<GameObject>();
        Resolve(allGameObjects);
    }

    /// <summary>
    /// Resolve a subset of the hierarchy downwards from a particular object.
    /// </summary>
    public void Resolve(GameObject parent)
    {
        var gameObjects = new GameObject[] { parent };
        Resolve(gameObjects);
    }

    /// <summary>
    /// Resolve dependencies for the hierarchy downwards from a set of game objects.
    /// </summary>
    public void Resolve(IEnumerable<GameObject> gameObjects)
    {
        var injectables = new List<MonoBehaviour>();
        var globalServices = new List<MonoBehaviour>();
        FindObjects(gameObjects, injectables, globalServices); // Scan the scene for objects of interest!

        foreach (var injectable in injectables)
        {
            ResolveDependencies(injectable, globalServices);
        }
    }
}
