﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using COMInteraction.InterfaceApplication.ReadValueConverters;
using COMInteraction.Misc;

namespace COMInteraction.InterfaceApplication
{
    /// <summary>
    /// Return a delegate that will take an object reference and return an implementation of a specified interface - any calls to properties or methods of that interface
    /// are passed straight through to the wrapped reference using Reflection. If any of the interface's properties or methods are not available on the wrapped reference
    /// then an exception will be thrown when requested. Any interfaces that the target interface implements will be accounted for. This can be used to wrap POCO objects
    /// or COM objects, for example. Note: Only handling interfaces, not classes, means there are less things to consider - there are no protected set methods or fields,
    /// for example. This implementation uses reflection to interact with the source data and so is recommended for use with .Net references.
    /// </summary>
    public class ReflectionInterfaceApplierFactory : IInterfaceApplierFactory
    {
        // ================================================================================================================================
        // CLASS INITIALISATION
        // ================================================================================================================================
        private bool _createComVisibleClasses;
		private DelayedExecutor<ModuleBuilder> _moduleBuilder;
        public ReflectionInterfaceApplierFactory(string assemblyName, ComVisibilityOptions comVisibilityOfClasses)
        {
            assemblyName = (assemblyName ?? "").Trim();
            if (assemblyName == "")
                throw new ArgumentException("Null or empty assemblyName specified");
            if (!Enum.IsDefined(typeof(ComVisibilityOptions), comVisibilityOfClasses))
                throw new ArgumentOutOfRangeException("comVisibilityOfClasses");
            
            _createComVisibleClasses = (comVisibilityOfClasses == ComVisibilityOptions.Visible);
			_moduleBuilder = new DelayedExecutor<ModuleBuilder>(
                () =>
                {
                    var assemblyBuilder = Thread.GetDomain().DefineDynamicAssembly(
                        new AssemblyName(assemblyName),
                        AssemblyBuilderAccess.Run
                    );
                    return assemblyBuilder.DefineDynamicModule(
                        assemblyBuilder.GetName().Name,
                        false
                    );
                }
            );
        }

		// ================================================================================================================================
        // IInterfaceApplierFactory IMPLEMENTATION
        // ================================================================================================================================
        /// <summary>
        /// Try to generate an InterfaceApplier for the specified interface (the targetType MUST be an interface, not a class), values returned from properties
        /// and methods will be passed through the specified readValueConverter in case manipulation is required (eg. applying an interface to particular
        /// returned property values of the targetType)
        /// </summary>
        public IInterfaceApplier GenerateInterfaceApplier(Type targetType, IReadValueConverter readValueConverter)
        {
            var generate = this.GetType().GetMethod("GenerateInterfaceApplier", new[] { typeof(IReadValueConverter) });
            var generateGeneric = generate.MakeGenericMethod(targetType);
            return (IInterfaceApplier)generateGeneric.Invoke(this, new[] { readValueConverter });
        }

        /// <summary>
        /// Try to generate an InterfaceApplier for the specified interface, values returned from properties and methods will be passed through the specified
        /// readValueConverter in case manipulation is required (eg. applying an interface to particular returned property values of the targetType)
        /// </summary>
        /// <typeparam name="T">This will always be an interface, never a class</typeparam>
        public IInterfaceApplier<T> GenerateInterfaceApplier<T>(IReadValueConverter readValueConverter)
        {
            if (!typeof(T).IsInterface)
                throw new ArgumentException("typeparam must be an interface type", "targetInterface");
            if (readValueConverter == null)
                throw new ArgumentNullException("readValueConverter");

			var typeName = "ReflectionInterfaceApplier" + Guid.NewGuid().ToString("N");
            var typeBuilder = _moduleBuilder.Value.DefineType(
                typeName,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit | TypeAttributes.AutoLayout,
                typeof(object),
                new Type[] { typeof(T) }
            );

            // ================================================================================================
            // Add [ComVisible(true)] and [ClassInterface(ClassInterfaceType.None)] attributes to the type so
            // that we can pass the generated objects through to COM object (eg. WSC controls) if desired
            // ================================================================================================
            if (_createComVisibleClasses)
            {
                typeBuilder.SetCustomAttribute(
                    new CustomAttributeBuilder(
                        typeof(ComVisibleAttribute).GetConstructor(new[] { typeof(bool) }),
                        new object[] { true }
                    )
                );
                typeBuilder.SetCustomAttribute(
                    new CustomAttributeBuilder(
                        typeof(ClassInterfaceAttribute).GetConstructor(new[] { typeof(ClassInterfaceType) }),
                        new object[] { ClassInterfaceType.None }
                    )
                );
            }

            // ================================================================================================
            // Ensure we account for any interfaces that the target interface implements (recursively)
            // ================================================================================================
            var interfaces = (new InterfaceHierarchyCombiner(typeof(T))).Interfaces;

            // ================================================================================================
            // Declare private "src" and "readValueConverter" fields
            // ================================================================================================
            var srcField = typeBuilder.DefineField("_src", typeof(object), FieldAttributes.Private);
            var readValueConverterField = typeBuilder.DefineField("_readValueConverter", typeof(IReadValueConverter), FieldAttributes.Private);

            // ================================================================================================
            // Generate the constructor, properties and methods (fields can't be declared on interfaces)
			// - We won't explicitly generate properties since "get_" and "set_" methods should be present on
			//   .Net classes which will be used automatically
            // ================================================================================================
            generateConstructor(typeBuilder, srcField, readValueConverterField);
            generateMethods<T>(typeBuilder, srcField, readValueConverterField, interfaces);

            // ================================================================================================
            // Return a new InterfaceApplier references
            // ================================================================================================
            return new InterfaceApplier<T>(
                src => (T)Activator.CreateInstance(
                    typeBuilder.CreateType(),
                    src,
                    readValueConverter
                )
            );
        }

        private static void generateConstructor(TypeBuilder typeBuilder, FieldBuilder srcField, FieldBuilder readValueConverterField)
        {
            if (typeBuilder == null)
                throw new ArgumentNullException("typeBuilder");
            if (srcField == null)
                throw new ArgumentNullException("srcField");
            if (readValueConverterField == null)
                throw new ArgumentNullException("readValueConverterField");
            if (!typeof(IReadValueConverter).IsAssignableFrom(readValueConverterField.FieldType))
                throw new ArgumentException("readValueConverterField must be assignable to IReadValueConverter");

            var ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                new[]
                {
                    typeof(object),
                    typeof(IReadValueConverter)
                }
            );
            var ilCtor = ctorBuilder.GetILGenerator();

            // Generate: base.ctor()
            ilCtor.Emit(OpCodes.Ldarg_0);
            ilCtor.Emit(OpCodes.Call, typeBuilder.BaseType.GetConstructor(Type.EmptyTypes));

            // Generate: if (src != null), don't throw new ArgumentException("src")
            var nonNullSrcArgumentLabel = ilCtor.DefineLabel();
            ilCtor.Emit(OpCodes.Ldarg_1);
            ilCtor.Emit(OpCodes.Brtrue, nonNullSrcArgumentLabel);
            ilCtor.Emit(OpCodes.Ldstr, "src");
            ilCtor.Emit(OpCodes.Newobj, typeof(ArgumentNullException).GetConstructor(new[] { typeof(string) }));
            ilCtor.Emit(OpCodes.Throw);
            ilCtor.MarkLabel(nonNullSrcArgumentLabel);

            // Generate: if (readValueConverter != null), don't throw new ArgumentException("readValueConverter")
            var nonNullReadValueConverterArgumentLabel = ilCtor.DefineLabel();
            ilCtor.Emit(OpCodes.Ldarg_2);
            ilCtor.Emit(OpCodes.Brtrue, nonNullReadValueConverterArgumentLabel);
            ilCtor.Emit(OpCodes.Ldstr, "readValueConverter");
            ilCtor.Emit(OpCodes.Newobj, typeof(ArgumentNullException).GetConstructor(new[] { typeof(string) }));
            ilCtor.Emit(OpCodes.Throw);
            ilCtor.MarkLabel(nonNullReadValueConverterArgumentLabel);

            // Generate: this._src = src
            ilCtor.Emit(OpCodes.Ldarg_0);
            ilCtor.Emit(OpCodes.Ldarg_1);
            ilCtor.Emit(OpCodes.Stfld, srcField);

            // Generate: this._readValueConverter = readValueConverter
            ilCtor.Emit(OpCodes.Ldarg_0);
            ilCtor.Emit(OpCodes.Ldarg_2);
            ilCtor.Emit(OpCodes.Stfld, readValueConverterField);

            // All done
            ilCtor.Emit(OpCodes.Ret);
        }

        private static void generateMethods<T>(TypeBuilder typeBuilder, FieldBuilder srcField, FieldBuilder readValueConverterField, NonNullImmutableList<Type> interfaces)
        {
            if (!typeof(T).IsInterface)
                throw new ArgumentException("typeparam must be an interface type", "targetInterface");
            if (typeBuilder == null)
                throw new ArgumentNullException("typeBuilder");
            if (srcField == null)
                throw new ArgumentNullException("srcField");
            if (readValueConverterField == null)
                throw new ArgumentNullException("readValueConverterField");
            if (!typeof(IReadValueConverter).IsAssignableFrom(readValueConverterField.FieldType))
                throw new ArgumentException("readValueConverterField must be assignable to IReadValueConverter");
            if (interfaces == null)
                throw new ArgumentNullException("interfaces");

            // There could be multiple methods defined with the same signature if they appear in multiple interfaces, this doesn't actually cause
            // any problems so we won't bother doing any work to prevent it happening
            // - While enumerating methods to implement, the get/set methods related to properties will be picked up here which is all we have
			//   do to deal with them (we don't have to generate property IL as well)
            foreach (var method in interfaces.SelectMany(i => i.GetMethods()))
            {
                var parameters = method.GetParameters();
                var parameterTypes = new List<Type>();
                foreach (var parameter in parameters)
                {
                    if (parameter.IsOut)
                        throw new ArgumentException("Output parameters are not supported");
                    if (parameter.IsOptional)
                        throw new ArgumentException("Optional parameters are not supported");
                    if (parameter.ParameterType.IsByRef)
                        throw new ArgumentException("Ref parameters are not supported");
                    parameterTypes.Add(parameter.ParameterType);
                }
                var funcBuilder = typeBuilder.DefineMethod(
                    method.Name,
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final,
                    method.ReturnType,
                    parameterTypes.ToArray()
                );
                var ilFunc = funcBuilder.GetILGenerator();

                // Generate: object[] args
                var argValues = ilFunc.DeclareLocal(typeof(object[]));

                // Generate: args = new object[x]
                ilFunc.Emit(OpCodes.Ldc_I4, parameters.Length);
                ilFunc.Emit(OpCodes.Newarr, typeof(Object));
                ilFunc.Emit(OpCodes.Stloc_0);
                for (var index = 0; index < parameters.Length; index++)
                {
                    // Generate: args[n] = ..;
                    var parameter = parameters[index];
                    ilFunc.Emit(OpCodes.Ldloc_0);
                    ilFunc.Emit(OpCodes.Ldc_I4, index);
                    ilFunc.Emit(OpCodes.Ldarg, index + 1);
                    if (parameter.ParameterType.IsValueType)
                        ilFunc.Emit(OpCodes.Box, parameter.ParameterType);
                    ilFunc.Emit(OpCodes.Stelem_Ref);
                }

                // Generate one of either:
                // 1. _src.GetType().InvokeMember(method.Name, BindingFlags.InvokeMethod, null, _src, args);
                // 2. return this._readValueConverter.Convert(
                //  method.DeclaringType.GetMethod(method.Name, {MethodArgTypes})
                //  this._src.GetType().InvokeMember(method.Name, BindingFlags.InvokeMethod, null, _src, args)
                // );
                var methodInfoInvokeMember = typeof(Type).GetMethod(
                    "InvokeMember",
                    new[]
                    {
                        typeof(string),
                        typeof(BindingFlags),
                        typeof(Binder),
                        typeof(object),
                        typeof(object[])
                    }
                );

                if (!method.ReturnType.Equals(typeof(void)))
                {
                    // Generate: Type[] argTypes
                    var argTypes = ilFunc.DeclareLocal(typeof(Type[]));

                    // Generate: argTypes = new Type[x]
                    ilFunc.Emit(OpCodes.Ldc_I4, parameters.Length);
                    ilFunc.Emit(OpCodes.Newarr, typeof(Type));
                    ilFunc.Emit(OpCodes.Stloc_1);
                    for (var index = 0; index < parameters.Length; index++)
                    {
                        // Generate: argTypes[n] = ..;
                        var parameter = parameters[index];
                        ilFunc.Emit(OpCodes.Ldloc_1);
                        ilFunc.Emit(OpCodes.Ldc_I4, index);
                        ilFunc.Emit(OpCodes.Ldtoken, parameters[index].ParameterType);
                        ilFunc.Emit(OpCodes.Stelem_Ref);
                    }

                    // Will call readValueConverter.Convert, passing MethodInfo reference before value
                    ilFunc.Emit(OpCodes.Ldarg_0);
                    ilFunc.Emit(OpCodes.Ldfld, readValueConverterField);
                    ilFunc.Emit(OpCodes.Ldtoken, method.DeclaringType);
                    ilFunc.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
                    ilFunc.Emit(OpCodes.Ldstr, method.Name);
                    ilFunc.Emit(OpCodes.Ldloc_1);
                    ilFunc.Emit(OpCodes.Call, typeof(Type).GetMethod("GetMethod", new[] { typeof(string), typeof(Type[]) }));
                }

                ilFunc.Emit(OpCodes.Ldarg_0);
                ilFunc.Emit(OpCodes.Ldfld, srcField);
                ilFunc.Emit(OpCodes.Callvirt, typeof(Type).GetMethod("GetType", Type.EmptyTypes));
                ilFunc.Emit(OpCodes.Ldstr, method.Name);
                ilFunc.Emit(OpCodes.Ldc_I4, (int)BindingFlags.InvokeMethod);
                ilFunc.Emit(OpCodes.Ldnull);
                ilFunc.Emit(OpCodes.Ldarg_0);
                ilFunc.Emit(OpCodes.Ldfld, srcField);
                ilFunc.Emit(OpCodes.Ldloc_0);
                ilFunc.Emit(OpCodes.Callvirt, methodInfoInvokeMember);

                if (!method.ReturnType.Equals(typeof(void)))
                    ilFunc.Emit(OpCodes.Callvirt, typeof(IReadValueConverter).GetMethod("Convert", new[] { typeof(MethodInfo), typeof(object) }));

                if (method.ReturnType.Equals(typeof(void)))
                    ilFunc.Emit(OpCodes.Pop);
                else if (method.ReturnType.IsValueType)
                    ilFunc.Emit(OpCodes.Unbox_Any, method.ReturnType);

                ilFunc.Emit(OpCodes.Ret);
            }
        }

        // ================================================================================================================================
        // IInterfaceApplier IMPLEMENTATION
        // ================================================================================================================================
        private class InterfaceApplier<T> : IInterfaceApplier<T>
        {
            private Func<object, T> _conversion;
            public InterfaceApplier(Func<object, T> conversion)
            {
                if (!typeof(T).IsInterface)
                    throw new ArgumentException("Invalid typeparam - must be an interface");
                if (conversion == null)
                    throw new ArgumentNullException("conversion");
                _conversion = conversion;
            }

            public Type TargetType
            {
                get { return typeof(T); }
            }

            public T Apply(object src)
            {
                return _conversion(src);
            }

            object IInterfaceApplier.Apply(object src)
            {
                return Apply(src);
            }
        }
    }
}
