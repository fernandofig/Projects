// Copyright 2006 Gokhan Altinoren - http://altinoren.com/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Altinoren.ActiveWriter.CodeGeneration
{
    using System;
    using System.CodeDom;
    using System.CodeDom.Compiler;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Xml;
    using Microsoft.VisualBasic;
    using EnvDTE;
    using Microsoft.VisualStudio.Modeling;
    using ServerExplorerSupport;
    using VSLangProj;
    using CodeNamespace = System.CodeDom.CodeNamespace;

    public class CodeGenerationHelper
    {
        #region Private Variables

        private CodeGenerationContext Context { get; set; }

        private Dictionary<string, string> _nHibernateConfigs = new Dictionary<string, string>();
        private Assembly _activeRecord;

        #endregion

        #region ctors

        public CodeGenerationHelper(CodeGenerationContext context)
        {
            Context = context;
        }

        #endregion

        #region Public Methods

        public void Generate()
        {
            if (Context.Language == CodeLanguage.VB)
            {
                // turn option strict on for VB if in project settings

                VSProject project = (VSProject) Context.ProjectItem.ContainingProject.Object;
                Property prop = project.Project.Properties.Item("OptionExplicit");

                if ((prjOptionStrict) prop.Value == prjOptionStrict.prjOptionStrictOn)
                {
                    Context.CompileUnit.UserData.Add("AllowLateBound", false);
                }
            }

            CodeNamespace nameSpace = new CodeNamespace(Context.Namespace);
            nameSpace.Imports.AddRange(Context.Model.NamespaceImports.ToArray());
            Context.CompileUnit.Namespaces.Add(nameSpace);

            foreach (ModelClass cls in Context.Model.Classes)
            {
                GenerateClass(cls, nameSpace);
            }

            foreach (NestedClass cls in Context.Model.NestedClasses)
            {
                GenerateNestedClass(cls, nameSpace);
            }

            if (Context.Model.GenerateMetaData != MetaDataGeneration.False)
            {
                GenerateMetaData(nameSpace);
            }

            GenerateHelperClass(nameSpace);

            if (Context.Model.Target == CodeGenerationTarget.ActiveRecord)
            {
                Context.PrimaryOutput = GenerateCode(Context.CompileUnit);

                if (Context.Model.UseNHQG)
                {
                    try
                    {
                        AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolve;
                        Assembly assembly = GenerateARAssembly(Context.CompileUnit, false);
                        UseNHQG(assembly);
                    }
                    finally
                    {
                        AppDomain.CurrentDomain.AssemblyResolve -= AssemblyResolve;
                    }
                }
            }
            else
            {
                Type starter = null;
                Assembly assembly = null;

                AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(AssemblyResolve);
                try
                {
                    _activeRecord = Assembly.Load(Context.Model.ActiveRecordAssemblyName);

                    // Code below means: ActiveRecordStarter.ModelsCreated += new ModelsCreatedDelegate(OnARModelCreated);
                    starter = _activeRecord.GetType("Castle.ActiveRecord.ActiveRecordStarter");
                    EventInfo eventInfo = starter.GetEvent("ModelsCreated");
                    Type eventType = eventInfo.EventHandlerType;
                    MethodInfo info =
                        this.GetType().GetMethod("OnARModelCreated", BindingFlags.Public | BindingFlags.Instance);
                    Delegate del = Delegate.CreateDelegate(eventType, this, info);
                    eventInfo.AddEventHandler(this, del);

                    assembly = GenerateARAssembly(Context.CompileUnit, !Context.Model.UseNHQG);
                }
                finally
                {
                    AppDomain.CurrentDomain.AssemblyResolve -= new ResolveEventHandler(AssemblyResolve);
                }

                // Code below means: ActiveRecordStarter.Initialize(assembly, new InPlaceConfigurationSource());
                Type config = _activeRecord.GetType("Castle.ActiveRecord.Framework.Config.InPlaceConfigurationSource");
                object configSource = Activator.CreateInstance(config);
                try
                {
                    starter.InvokeMember("ResetInitializationFlag",
                                         BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod, null, null, null);
                    starter.InvokeMember("Initialize",
                                         BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod, null,
                                         null,
                                         new object[] { assembly, configSource });
                }
                catch (TargetInvocationException ex)
                {
                    // Eat config errors
                    if (!ex.InnerException.Message.StartsWith("Could not find configuration for"))
                        throw;
                }
                ClearARAttributes(Context.CompileUnit);
                Context.PrimaryOutput = GenerateCode(Context.CompileUnit);

                foreach (KeyValuePair<string, string> pair in _nHibernateConfigs)
                {
                    string path = Path.Combine(Context.ModelFilePath, RemoveNamespaceFromStart(pair.Key) + ".hbm.xml");
                    using (StreamWriter writer = new StreamWriter(path, false, Encoding.Unicode))
                    {
                        writer.Write(pair.Value);
                    }

                    AddToProject(path, prjBuildAction.prjBuildActionEmbeddedResource);
                }

                if (Context.Model.UseNHQG)
                {
                   UseNHQG(assembly);
                }
            }
        }

        #endregion

        #region Private Methods

        #region Class

        private CodeTypeDeclaration GetClassDeclaration(ModelClass cls, CodeNamespace nameSpace)
        {
            if (cls == null)
                throw new ArgumentException("Class not supplied.", "cls");
            if (String.IsNullOrEmpty(cls.Name))
                throw new ArgumentException("Class name cannot be blank.", "cls");

            string className = cls.Name;
            if (cls.Model.GeneratesDoubleDerived)
                className += cls.Model.DoubleDerivedNameSuffix;

            CodeTypeDeclaration classDeclaration = CreateClass(className);

            if (cls.Model.GeneratesDoubleDerived)
                classDeclaration.TypeAttributes |= TypeAttributes.Abstract;

            if (cls.Model.UseBaseClass)
            {
                bool withValidator = cls.Properties.FindAll(delegate(ModelProperty property)
                {
                    return property.IsValidatorSet();
                }).Count > 0;

                CodeTypeReference type;
                // base class for every modelclass. If left empty then baseclass from model if left empty ...etc
                if (!string.IsNullOrEmpty(cls.BaseClassName))
                    type = new CodeTypeReference(cls.BaseClassName);
                else if (!string.IsNullOrEmpty(cls.Model.BaseClassName))
                    type = new CodeTypeReference(cls.Model.BaseClassName);
                else if (withValidator)
                    type = new CodeTypeReference(Common.DefaultValidationBaseClass);
                else
                    type = new CodeTypeReference(Common.DefaultBaseClass);

                if (cls.IsGeneric())
                    type.TypeArguments.Add(cls.Name);
                classDeclaration.BaseTypes.Add(type);
            }

            if (cls.DoesImplementINotifyPropertyChanged())
            {
                classDeclaration.BaseTypes.Add(new CodeTypeReference(Common.INotifyPropertyChangedType));
                AddINotifyPropertyChangedRegion(classDeclaration);
            }
            if (cls.DoesImplementINotifyPropertyChanging())
            {
                classDeclaration.BaseTypes.Add(new CodeTypeReference(Common.INotifyPropertyChangingType));
                AddINotifyPropertyChangingRegion(classDeclaration);
            }

            if (!String.IsNullOrEmpty(cls.Description))
                classDeclaration.Comments.AddRange(GetSummaryComment(cls.Description));

            if (!cls.Model.GeneratesDoubleDerived)
                classDeclaration.CustomAttributes.Add(cls.GetActiveRecordAttribute());
            if (cls.Model.UseGeneratedCodeAttribute)
                classDeclaration.CustomAttributes.Add(AttributeHelper.GetGeneratedCodeAttribute());

            nameSpace.Types.Add(classDeclaration);
            return classDeclaration;
        }

        private CodeTypeDeclaration GetNestedClassDeclaration(NestedClass cls, CodeNamespace nameSpace)
        {
            if (cls == null)
                throw new ArgumentException("Nested class not supplied.", "cls");
            if (String.IsNullOrEmpty(cls.Name))
                throw new ArgumentException("Nested class name cannot be blank.", "cls");

            CodeTypeDeclaration classDeclaration = CreateClass(cls.Name);
            if (cls.DoesImplementINotifyPropertyChanged())
            {
                classDeclaration.BaseTypes.Add(new CodeTypeReference(Common.INotifyPropertyChangedType));
                AddINotifyPropertyChangedRegion(classDeclaration);
            }
            if (cls.DoesImplementINotifyPropertyChanging())
            {
                classDeclaration.BaseTypes.Add(new CodeTypeReference(Common.INotifyPropertyChangingType));
                AddINotifyPropertyChangingRegion(classDeclaration);
            }
            if (!String.IsNullOrEmpty(cls.Description))
                classDeclaration.Comments.AddRange(GetSummaryComment(cls.Description));

            if (cls.Model.UseGeneratedCodeAttribute)
                classDeclaration.CustomAttributes.Add(AttributeHelper.GetGeneratedCodeAttribute());

            nameSpace.Types.Add(classDeclaration);
            return classDeclaration;
        }

        private CodeTypeDeclaration GetCompositeClassDeclaration(CodeNamespace nameSpace,
                                                                 CodeTypeDeclaration parentClass,
                                                                 List<ModelProperty> keys, bool implementINotifyPropertyChanged)
        {
            if (keys == null || keys.Count <= 1)
                throw new ArgumentException("Composite keys must consist of two or more properties.", "keys");

            string className = null;
            foreach (ModelProperty property in keys)
            {
                if (!string.IsNullOrEmpty(property.CompositeKeyName))
                {
                    className = property.CompositeKeyName;
                    break;
                }
            }

            if (string.IsNullOrEmpty(className))
                className = parentClass.Name + Common.CompositeClassNameSuffix;

            CodeTypeDeclaration classDeclaration = CreateClass(className);

            if (implementINotifyPropertyChanged)
            {
                classDeclaration.BaseTypes.Add(new CodeTypeReference(Common.INotifyPropertyChangedType));
                AddINotifyPropertyChangedRegion(classDeclaration);
            }

            classDeclaration.CustomAttributes.Add(new CodeAttributeDeclaration("Serializable"));
            if (keys[0].ModelClass.Model.UseGeneratedCodeAttribute)
                classDeclaration.CustomAttributes.Add(AttributeHelper.GetGeneratedCodeAttribute());

            List<CodeMemberField> fields = new List<CodeMemberField>();

            List<string> descriptions = new List<string>();

            foreach (ModelProperty property in keys)
            {
                CodeMemberField memberField = GetMemberFieldOfProperty(property, Accessor.Private);
                classDeclaration.Members.Add(memberField);
                fields.Add(memberField);

                classDeclaration.Members.Add(GetActiveRecordMemberKeyProperty(memberField, property));

                if (!String.IsNullOrEmpty(property.Description))
                    descriptions.Add(property.Description);
            }

            if (descriptions.Count > 0)
                classDeclaration.Comments.AddRange(GetSummaryComment(descriptions.ToArray()));

            classDeclaration.Members.Add(GetCompositeClassToStringMethod(fields));
            classDeclaration.Members.Add(GetCompositeClassEqualsMethod(className, fields));
            classDeclaration.Members.AddRange(GetCompositeClassGetHashCodeMethods(fields));

            nameSpace.Types.Add(classDeclaration);
            return classDeclaration;
        }


        private CodeTypeDeclaration CreateClass(string name)
        {
            CodeTypeDeclaration classDeclaration = new CodeTypeDeclaration(name);
            classDeclaration.TypeAttributes = TypeAttributes.Public;
            classDeclaration.IsPartial = true;
            return classDeclaration;
        }

        #endregion

        #region Method

        private CodeTypeMember[] GetCompositeClassGetHashCodeMethods(List<CodeMemberField> fields)
        {
            //public override int GetHashCode()
            //{
            //  return _keyA.GetHashCode() ^ _keyB.GetHashCode();
            //}

            CodeTypeMember[] methods = new CodeTypeMember[2];

            CodeMemberMethod getHashCode = new CodeMemberMethod();
            getHashCode.Attributes = MemberAttributes.Public | MemberAttributes.Override;
            getHashCode.ReturnType = new CodeTypeReference(typeof(Int32));
            getHashCode.Name = "GetHashCode";

            List<CodeExpression> expressions = new List<CodeExpression>();
            foreach (CodeMemberField field in fields)
            {
                expressions.Add(
                    new CodeMethodInvokeExpression(new CodeFieldReferenceExpression(null, field.Name), "GetHashCode"));
            }

            // Now, there's no CodeBinaryOperatorType.XOr or something. We write a helper method instead.
            CodeMemberMethod xor = new CodeMemberMethod();
            xor.Attributes = MemberAttributes.Private;
            xor.ReturnType = new CodeTypeReference(typeof(Int32));
            xor.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Int32), "left"));
            xor.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Int32), "right"));
            xor.Name = Common.XorHelperMethod;
            if (Context.Provider is VBCodeProvider)
                xor.Statements.Add(new CodeMethodReturnStatement(new CodeSnippetExpression("left XOR right")));
            else
                xor.Statements.Add(new CodeMethodReturnStatement(new CodeSnippetExpression("left ^ right")));

            CodeExpression expression;
            if (expressions.Count > 2)
                expression =
                    new CodeMethodInvokeExpression(null, Common.XorHelperMethod, expressions[0], GetXor(expressions, 1));
            else
                expression =
                    new CodeMethodInvokeExpression(null, Common.XorHelperMethod, expressions[0], expressions[1]);

            getHashCode.Statements.Add(new CodeMethodReturnStatement(expression));

            methods[0] = getHashCode;
            methods[1] = xor;

            return methods;
        }

        private CodeTypeMember GetCompositeClassEqualsMethod(string className, List<CodeMemberField> fields)
        {
            //public override bool Equals( object obj )
            //{
            //    if( obj == this ) return true;
            //    if( obj == null || obj.GetType() != this.GetType() ) return false;
            //    MyCompositeKey test = ( MyCompositeKey ) obj;
            //    return ( _keyA == test.KeyA || (_keyA != null && _keyA.Equals( test.KeyA ) ) ) &&
            //      ( _keyB == test.KeyB || ( _keyB != null && _keyB.Equals( test.KeyB ) ) );
            //}
            CodeMemberMethod equals = new CodeMemberMethod
                                          {
                                              Attributes = (MemberAttributes.Public | MemberAttributes.Override),
                                              ReturnType = new CodeTypeReference(typeof (Boolean)),
                                              Name = "Equals"
                                          };

            CodeParameterDeclarationExpression param = new CodeParameterDeclarationExpression(typeof(Object), "obj");
            equals.Parameters.Add(param);

            equals.Statements.Add(new CodeConditionStatement(
                                      new CodeBinaryOperatorExpression(
                                          new CodeFieldReferenceExpression(null, "obj"),
                                          CodeBinaryOperatorType.ValueEquality, new CodeThisReferenceExpression()
                                          ), new CodeMethodReturnStatement(new CodePrimitiveExpression(true))
                                      )
                );

            equals.Statements.Add(new CodeConditionStatement
                                      (
                                      new CodeBinaryOperatorExpression
                                          (
                                          new CodeBinaryOperatorExpression(
                                              new CodeFieldReferenceExpression(null, "obj"),
                                              CodeBinaryOperatorType.ValueEquality, new CodePrimitiveExpression(null)),
                                          CodeBinaryOperatorType.BooleanOr,
                                          new CodeBinaryOperatorExpression(
                                              new CodeMethodInvokeExpression(
                                                  new CodeFieldReferenceExpression(null, "obj"), "GetType"),
                                              CodeBinaryOperatorType.IdentityInequality,
                                              new CodeMethodInvokeExpression(new CodeThisReferenceExpression(),
                                                                             "GetType"))
                                          )
                                      , new CodeMethodReturnStatement(new CodePrimitiveExpression(false))
                                      )
                );

            equals.Statements.Add(
                new CodeVariableDeclarationStatement(new CodeTypeReference(className), "test",
                                                     new CodeCastExpression(new CodeTypeReference(className),
                                                                            new CodeFieldReferenceExpression(null, "obj"))));

            List<CodeExpression> expressions = new List<CodeExpression>();
            foreach (CodeMemberField field in fields)
            {
                expressions.Add(
                    new CodeBinaryOperatorExpression(
                    //_keyA == test.KeyA
                        new CodeBinaryOperatorExpression(
                            new CodeFieldReferenceExpression(null, field.Name),
                            CodeBinaryOperatorType.ValueEquality,
                            new CodeFieldReferenceExpression(new CodeFieldReferenceExpression(null, "test"), field.Name)),
                        CodeBinaryOperatorType.BooleanOr, // ||
                        new CodeBinaryOperatorExpression(
                    //_keyA != null
                            new CodeBinaryOperatorExpression(
                                new CodeFieldReferenceExpression(null, field.Name),
                                CodeBinaryOperatorType.IdentityInequality,
                                new CodePrimitiveExpression(null)
                                ),
                            CodeBinaryOperatorType.BooleanAnd, // &&
                    // _keyA.Equals( test.KeyA )   
                            new CodeMethodInvokeExpression(
                                new CodeFieldReferenceExpression(null, field.Name), "Equals",
                                new CodeFieldReferenceExpression(
                                    new CodeFieldReferenceExpression(null, "test"), field.Name))
                            )
                        )
                    );
            }

            CodeExpression expression;
            if (expressions.Count > 2)
                expression =
                    new CodeBinaryOperatorExpression(expressions[0], CodeBinaryOperatorType.BooleanAnd,
                                                     GetBooleanAnd(expressions, 1));
            else
                expression =
                    new CodeBinaryOperatorExpression(expressions[0], CodeBinaryOperatorType.BooleanAnd, expressions[1]);


            equals.Statements.Add(new CodeMethodReturnStatement(expression));

            return equals;
        }

        private static CodeMemberMethod GetCompositeClassToStringMethod(List<CodeMemberField> fields)
        {
            //public override string ToString()
            //{
            //  return string.Join( ":", new string[] { _keyA, _keyB } );
            //}
            CodeMemberMethod toString = new CodeMemberMethod
                                            {
                                                Attributes = (MemberAttributes.Public | MemberAttributes.Override),
                                                ReturnType = new CodeTypeReference(typeof (String)),
                                                Name = "ToString"
                                            };

            CodeExpression[] expressions = new CodeExpression[fields.Count];
            for (int i = 0; i < fields.Count; i++)
            {
                expressions[i] =
                    new CodeMethodInvokeExpression(
                        new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), fields[i].Name), "ToString");
            }

            toString.Statements.Add(new CodeMethodReturnStatement(
                                        new CodeMethodInvokeExpression(new CodeTypeReferenceExpression("String"), "Join",
                                                                       new CodeSnippetExpression("\":\""),
                                                                       new CodeArrayCreateExpression(typeof(String),
                                                                                                     expressions)))
                );
            return toString;
        }

        #endregion

        #region Property

        // TODO: All this property generation is a total mess. Lots of similar methods. Hard to find what you're looking for. Hard to change.

        private CodeTypeMember GetActiveRecordMemberCompositeKeyProperty(CodeTypeDeclaration compositeClass,
                                                                         CodeMemberField memberField, bool implementsINotifyPropertyChanged, bool implementsINotifyPropertyChanging)
        {
            // TODO: Composite key generation omits UnsavedValue property. All the properties which are parts of the composite key
            // should have the same UnsavedValue, by the way.
            CodeMemberProperty memberProperty =
                GetMemberProperty(memberField, compositeClass.Name, true, true, implementsINotifyPropertyChanged, implementsINotifyPropertyChanging, null);

            memberProperty.CustomAttributes.Add(new CodeAttributeDeclaration("CompositeKey"));

            return memberProperty;
        }

        private CodeMemberProperty GetActiveRecordMemberKeyProperty(CodeMemberField memberField,
                                                                    ModelProperty property)
        {
            CodeMemberProperty memberProperty =
                GetMemberProperty(memberField, property.Name, property.ColumnType, null, property.NotNull, true, true, property.ImplementsINotifyPropertyChanged(), property.ImplementsINotifyPropertyChanging(), property.Description);
            memberProperty.CustomAttributes.Add(property.GetKeyPropertyAttribute());

            return memberProperty;
        }

        private CodeMemberProperty GetActiveRecordMemberProperty(CodeMemberField memberField,
                                                                 ModelProperty property)
        {
            CodeMemberProperty memberProperty =
                GetMemberProperty(memberField, property.Name, property.ColumnType,
								  property.CustomMemberType,
								  property.NotNull,
                                  true,
                                  true,
                                  property.ImplementsINotifyPropertyChanged(),
                                  property.ImplementsINotifyPropertyChanging(),
                                  property.Description);
            CodeAttributeDeclaration attributeDecleration = null;

            switch (property.KeyType)
            {
                // Composite keys must be handled in upper levels
                case KeyType.None:
                    attributeDecleration = property.GetPropertyAttribute();
                    break;
                case KeyType.PrimaryKey:
                    attributeDecleration = property.GetPrimaryKeyAttribute();
                    break;
            }

            memberProperty.CustomAttributes.Add(attributeDecleration);

            return memberProperty;
        }

        private CodeMemberProperty GetActiveRecordMemberVersion(CodeMemberField memberField,
                                                                ModelProperty property)
        {
            CodeMemberProperty memberProperty =
                GetMemberProperty(memberField, property.Name, property.ColumnType, null, property.NotNull, true, true, property.ImplementsINotifyPropertyChanged(), property.ImplementsINotifyPropertyChanging(),
                                  property.Description);
            memberProperty.CustomAttributes.Add(property.GetVersionAttribute());

            return memberProperty;
        }

        private CodeMemberProperty GetActiveRecordMemberTimestamp(CodeMemberField memberField,
                                                                  ModelProperty property)
        {
            CodeMemberProperty memberProperty =
                GetMemberProperty(memberField, property.Name, property.ColumnType, null, property.NotNull, true, true, property.ImplementsINotifyPropertyChanged(), property.ImplementsINotifyPropertyChanging(),
                                  property.Description);
            memberProperty.CustomAttributes.Add(property.GetTimestampAttribute());

            return memberProperty;
        }

		private CodeMemberProperty GetMemberProperty(CodeMemberField memberField, string propertyName,
												NHibernateType propertyType, string propertyCustomMemberType, bool propertyNotNull,
                                                bool get, bool set, bool implementINotifyPropertyChanged, bool implementINotifyPropertyChanging, params string[] description)
		{
            CodeMemberProperty memberProperty =
                GetMemberPropertyWithoutType(memberField, propertyName, get, set, implementINotifyPropertyChanged, implementINotifyPropertyChanging, description);

			if (Context.Model.UseNullables != NullableUsage.No && TypeHelper.IsNullable(propertyType) && !propertyNotNull)
            {
                if (Context.Model.UseNullables == NullableUsage.WithHelperLibrary)
                    memberProperty.Type = TypeHelper.GetNullableTypeReferenceForHelper(propertyType);
                else
                    memberProperty.Type = TypeHelper.GetNullableTypeReference(propertyType);
            }
            else
				memberProperty.Type = new CodeTypeReference(TypeHelper.GetSystemType(propertyType, propertyCustomMemberType));

            return memberProperty;
        }

        private CodeMemberProperty GetMemberProperty(CodeMemberField memberField, string propertyName,
                                                     bool get, bool set, bool implementINotifyPropertyChanged, bool implementINotifyPropertyChanging, params string[] description)
        {
            CodeMemberProperty memberProperty =
                GetMemberPropertyWithoutType(memberField, propertyName, get, set, implementINotifyPropertyChanged, implementINotifyPropertyChanging, description);

            memberProperty.Type = memberField.Type;

            return memberProperty;
        }

        private CodeMemberProperty GetMemberPropertyWithoutType(CodeMemberField memberField, string propertyName,
                                                                bool get, bool set, bool implementINotifyPropertyChanged, bool implementINotifyPropertyChanging, params string[] description)
        {
            CodeMemberProperty memberProperty = new CodeMemberProperty();

            memberProperty.Name = propertyName;

            if (Context.Model.UseVirtualProperties)
                memberProperty.Attributes = MemberAttributes.Public;
            else
                memberProperty.Attributes = MemberAttributes.Public | MemberAttributes.Final;
            if (get)
                memberProperty.GetStatements.Add(new CodeMethodReturnStatement(
                                                     new CodeFieldReferenceExpression(
                                                         new CodeThisReferenceExpression(), memberField.Name)));
            if (set)
            {
                var assignValue = new CodeAssignStatement(
                                    new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), memberField.Name),
                                    new CodeArgumentReferenceExpression("value")
                                    );

                if (!implementINotifyPropertyChanged && !implementINotifyPropertyChanging)
                {
                    memberProperty.SetStatements.Add(assignValue);
                }
                else
                {
                    // There's no ValueInequality in CodeDOM (ridiculous), so we'll use ((a == b) == false) instead.
                    var equalityCheck = new CodeBinaryOperatorExpression(
                                            new CodeBinaryOperatorExpression(
                                                new CodeFieldReferenceExpression(null, memberField.Name),
                                                CodeBinaryOperatorType.ValueEquality,
                                                new CodeArgumentReferenceExpression("value")),
                                            CodeBinaryOperatorType.ValueEquality,
                                            new CodePrimitiveExpression(false)
                                        );

                    var assignment = new CodeConditionStatement(equalityCheck, assignValue);
                    memberProperty.SetStatements.Add(assignment);

                    if (implementINotifyPropertyChanged)
                    {
                        assignment.TrueStatements.Add(
                            new CodeExpressionStatement(
                                new CodeMethodInvokeExpression(
                                    new CodeMethodReferenceExpression(new CodeThisReferenceExpression(),
                                                                             Common.PropertyChangedInternalMethod),
                                           new CodePrimitiveExpression(propertyName)
                                           ))
                            );
                    }
                    if (implementINotifyPropertyChanging)
                    {
                        assignment.TrueStatements.Insert(0,
                            new CodeExpressionStatement(
                                new CodeMethodInvokeExpression(
                                    new CodeMethodReferenceExpression(new CodeThisReferenceExpression(),
                                                                             Common.PropertyChangingInternalMethod),
                                           new CodePrimitiveExpression(propertyName)
                                           ))
                            );
                    }
                }
            }

            if (description != null && description.Length > 0)
                memberProperty.Comments.AddRange(GetSummaryComment(description));

            return memberProperty;
        }

        #endregion

        #region Field

        private CodeMemberField GetPrivateMemberFieldOfCompositeClass(CodeTypeDeclaration compositeClass, PropertyAccess access)
        {
            return GetMemberField(compositeClass.Name, compositeClass.Name, Accessor.Private, access);
        }

        private CodeMemberField GetMemberFieldOfProperty(ModelProperty property, Accessor accessor)
        {
            if (Context.Model.UseNullables != NullableUsage.No && TypeHelper.IsNullable(property.ColumnType) && !property.NotNull)
			{
                if (Context.Model.UseNullables == NullableUsage.WithHelperLibrary)
                    return GetMemberField(property.Name, TypeHelper.GetNullableTypeReferenceForHelper(property.ColumnType), accessor, property.Access);
			    
                return GetMemberField(property.Name, TypeHelper.GetNullableTypeReference(property.ColumnType), accessor, property.Access);
			}

            return GetMemberField(property.Name, TypeHelper.GetSystemType(property.ColumnType, property.CustomMemberType), accessor, property.Access);
        }

        private CodeMemberField GetMemberField(CodeTypeDeclaration classDeclaration, ModelProperty property)
        {
            // Soooo ugly.
            CodeMemberField memberField = null;
            switch (property.PropertyType)
            {
                case PropertyType.Property:
                    memberField = GetMemberFieldOfProperty(property, Accessor.Private);
                    CodeMemberProperty memberProperty = GetActiveRecordMemberProperty(memberField, property);
                    classDeclaration.Members.Add(memberProperty);
                    if (property.IsValidatorSet())
                        memberProperty.CustomAttributes.AddRange(property.GetValidationAttributes());
                    break;
                case PropertyType.Field:
                    memberField = GetMemberFieldOfProperty(property, property.Accessor);
                    memberField.CustomAttributes.Add(property.GetFieldAttribute());
                    break;
                case PropertyType.Version:
                    memberField = GetMemberFieldOfProperty(property, Accessor.Private);
                    classDeclaration.Members.Add(GetActiveRecordMemberVersion(memberField, property));
                    break;
                case PropertyType.Timestamp:
                    memberField = GetMemberFieldOfProperty(property, Accessor.Private);
                    classDeclaration.Members.Add(GetActiveRecordMemberTimestamp(memberField, property));
                    break;
            }
            return memberField;
        }

        private CodeMemberField GetMemberField(string name, CodeTypeReference fieldType, Accessor accessor, PropertyAccess access)
        {
            CodeMemberField memberField = GetMemberFieldWithoutType(name, accessor, access);

            memberField.Type = fieldType;

            return memberField;
        }

        private CodeMemberField GetMemberField(string name, string fieldType, Accessor accessor, PropertyAccess access)
        {
            CodeMemberField memberField = GetMemberFieldWithoutType(name, accessor, access);

            memberField.Type = new CodeTypeReference(fieldType);

            return memberField;
        }

        private CodeMemberField GetGenericMemberField(string typeName, string name, string fieldType, Accessor accessor, PropertyAccess access)
        {
            CodeMemberField memberField = GetMemberFieldWithoutType(name, accessor, access);

            CodeTypeReference type = new CodeTypeReference(fieldType);
            if (!TypeHelper.ContainsGenericDecleration(fieldType, Context.Language))
            {
                type.TypeArguments.Add(typeName);
            }
            memberField.Type = type;

            return memberField;
        }

        private CodeMemberField GetMemberFieldWithoutType(string name, Accessor accessor, PropertyAccess access)
        {
            CodeMemberField memberField = GetMemberFieldWithoutTypeAndName(accessor);

            memberField.Name = NamingHelper.GetName(name, access, Context.Model.CaseOfPrivateFields);

            return memberField;
        }

        private CodeMemberField GetMemberFieldWithoutTypeAndName(Accessor accessor)
        {
            CodeMemberField memberField = new CodeMemberField();

            switch (accessor)
            {
                case Accessor.Public:
                    memberField.Attributes = MemberAttributes.Public;
                    break;
                case Accessor.Protected:
                    memberField.Attributes = MemberAttributes.Family;
                    break;
                case Accessor.Private:
                    memberField.Attributes = MemberAttributes.Private;
                    break;
            }
            return memberField;
        }

        private CodeMemberField GetStaticMemberFieldWithoutTypeAndName(Accessor accessor)
        {
            CodeMemberField memberField = new CodeMemberField();

            switch (accessor)
            {
                case Accessor.Public:
                    memberField.Attributes = MemberAttributes.Public | MemberAttributes.Static;
                    break;
                case Accessor.Protected:
                    memberField.Attributes = MemberAttributes.Family | MemberAttributes.Static;
                    break;
                case Accessor.Private:
                    memberField.Attributes = MemberAttributes.Private | MemberAttributes.Static;
                    break;
            }
            return memberField;
        }

        #endregion

        #region Relation

        #region HasMany

        private void GenerateHasManyRelation(CodeTypeDeclaration classDeclaration, CodeNamespace nameSpace,
                                             ManyToOneRelation relationship)
        {
            if (relationship.TargetPropertyGenerated)
                GenerateHasMany(
                    classDeclaration,
                    relationship.Source.Name,
                    relationship.TargetPropertyName,
                    relationship.TargetPropertyType,
                    relationship.TargetAccess,
                    relationship.TargetDescription,
                    relationship.GetHasManyAttribute(),
                    relationship.Source.AreRelationsGeneric(),
                    relationship.Target.DoesImplementINotifyPropertyChanged(),
                    relationship.Target.DoesImplementINotifyPropertyChanging(),
                    null);
        }

        private void GenerateHasMany(CodeTypeDeclaration classDeclaration, string className, string customPropertyName, string customPropertyType, PropertyAccess propertyAccess, string description, CodeAttributeDeclaration attribute, bool genericRelation, bool propertyChanged, bool propertyChanging, CodeAttributeDeclaration collectionIdAttribute)
        {
            // The customPropertyName used to be pluralized in HasMany and in only one side of HasAndBelongsToMany.
            // It makes more sense to not pluralize the property since it allows users to avoid the automatic pluralization if desired.
            // This allows users to create properties like "Children" and "Parent" rather than something strange like "Childs".
            string propertyName = String.IsNullOrEmpty(customPropertyName)
                                      ? NamingHelper.GetPlural(className)
                                      : customPropertyName;
            string propertyType = String.IsNullOrEmpty(customPropertyType)
                                      ? Context.Model.EffectiveListInterface
                                      : customPropertyType;

            CodeMemberField memberField;
            if (!genericRelation)
                memberField = GetMemberField(propertyName, propertyType, Accessor.Private, propertyAccess);
            else
                memberField = GetGenericMemberField(className, propertyName, propertyType, Accessor.Private, propertyAccess);
            classDeclaration.Members.Add(memberField);

            // Initializes the collection by assigning a new list instance to the field.
            // Many-to-many relationships never had the initialization code enabled before.
            // Automatic associations initialize their lists in the constructor instead.
            if (Context.Model.InitializeIListFields && propertyType == Context.Model.EffectiveListInterface && !Context.Model.AutomaticAssociations)
            {
                CodeObjectCreateExpression fieldCreator = new CodeObjectCreateExpression();
                fieldCreator.CreateType = GetConcreteListType(className);
                memberField.InitExpression = fieldCreator;
            }

            if (description == "") description = null;
            CodeMemberProperty memberProperty = GetMemberProperty(memberField, propertyName, true, true, propertyChanged, propertyChanging, description);
                               
            classDeclaration.Members.Add(memberProperty);

            memberProperty.CustomAttributes.Add(attribute);

            if (collectionIdAttribute != null)
                memberProperty.CustomAttributes.Add(collectionIdAttribute);
        }

        #endregion

        #region BelongsTo

        private void GenerateBelongsToRelation(CodeTypeDeclaration classDeclaration, CodeNamespace nameSpace,
                                               ManyToOneRelation relationship)
        {
            if (relationship.SourcePropertyGenerated)
            {
                if (!String.IsNullOrEmpty(relationship.TargetColumnKey) && (!String.IsNullOrEmpty(relationship.SourceColumn)) &&
                    !relationship.SourceColumn.ToUpperInvariant().Equals(relationship.TargetColumnKey.ToUpperInvariant()))
                    throw new ArgumentException(
                        String.Format(
                            "Class {0} column name does not match with column key {1} on it's many to one relation to class {2}",
                            relationship.Source.Name, relationship.TargetColumnKey, relationship.Target.Name));

                string propertyName = String.IsNullOrEmpty(relationship.SourcePropertyName)
                                          ? relationship.Target.Name
                                          : relationship.SourcePropertyName;

                // We use PropertyAccess.Property to default it to camel case underscore
                CodeMemberField memberField = GetMemberField(propertyName, relationship.Target.Name, Accessor.Private, PropertyAccess.Property);
                classDeclaration.Members.Add(memberField);

                CodeMemberProperty memberProperty;
                if (String.IsNullOrEmpty(relationship.SourceDescription))
                    memberProperty = GetMemberProperty(memberField, propertyName, true, true, relationship.Source.DoesImplementINotifyPropertyChanged(), relationship.Source.DoesImplementINotifyPropertyChanging(), null);
                else
                    memberProperty =
                        GetMemberProperty(memberField, propertyName, true, true, relationship.Source.DoesImplementINotifyPropertyChanged(), relationship.Source.DoesImplementINotifyPropertyChanging(),
                                          relationship.SourceDescription);
                classDeclaration.Members.Add(memberProperty);

                memberProperty.CustomAttributes.Add(relationship.GetBelongsToAttribute());
            }
        }

        #endregion

        #region HasAndBelongsToRelation

        private void GenerateHasAndBelongsToRelationFromTargets(CodeTypeDeclaration classDeclaration, CodeNamespace nameSpace,
                                               ManyToManyRelation relationship)
        {
            if (relationship.SourcePropertyGenerated)
                GenerateHasManyToMany(
                    classDeclaration,
                    relationship.Target.Name,
                    relationship.SourcePropertyName,
                    relationship.SourcePropertyType,
                    relationship.SourceAccess,
                    relationship.SourceDescription,
                    relationship.GetHasAndBelongsToAttributeFromSource(),
                    relationship.Target.AreRelationsGeneric(),
                    relationship.Source.DoesImplementINotifyPropertyChanged(),
                    relationship.Source.DoesImplementINotifyPropertyChanging(),
                    relationship.EffectiveSourceRelationType,
                    relationship);
        }

        private void GenerateHasAndBelongsToRelationFromSources(CodeTypeDeclaration classDeclaration, CodeNamespace nameSpace,
                                               ManyToManyRelation relationship)
        {
            if (relationship.TargetPropertyGenerated)
                GenerateHasManyToMany(
                    classDeclaration,
                    relationship.Source.Name,
                    relationship.TargetPropertyName,
                    relationship.TargetPropertyType,
                    relationship.TargetAccess,
                    relationship.TargetDescription,
                    relationship.GetHasAndBelongsToAttributeFromTarget(),
                    relationship.Source.AreRelationsGeneric(),
                    relationship.Target.DoesImplementINotifyPropertyChanged(),
                    relationship.Target.DoesImplementINotifyPropertyChanging(),
                    relationship.EffectiveTargetRelationType,
                    relationship);
        }

        private void GenerateHasManyToMany(CodeTypeDeclaration classDeclaration, string className, string customPropertyName, string customPropertyType, PropertyAccess propertyAccess, string description, CodeAttributeDeclaration attribute, bool genericRelation, bool propertyChanged, bool propertyChanging, Altinoren.ActiveWriter.RelationType relationType, ManyToManyRelation relationship)
        {
            if (String.IsNullOrEmpty(relationship.Table))
                throw new ArgumentNullException(
                    String.Format("Class {0} does not have a table name on it's many to many relation to class {1}",
                                  relationship.Source.Name, relationship.Target.Name));
            if (String.IsNullOrEmpty(relationship.SourceColumn))
                throw new ArgumentNullException(
                    String.Format("Class {0} does not have a source column name on it's many to many relation to class {1}",
                                  relationship.Source.Name, relationship.Target.Name));
            if (String.IsNullOrEmpty(relationship.TargetColumn))
                throw new ArgumentNullException(
                    String.Format("Class {0} does not have a target column name on it's many to many relation to class {1}",
                                  relationship.Source.Name, relationship.Target.Name));

            GenerateHasMany(classDeclaration, className, customPropertyName, customPropertyType, propertyAccess, description, attribute, genericRelation, propertyChanged, propertyChanging, relationship.GetCollectionIdAttribute(relationType));
        }

        #endregion

        #region OneToOne

        private void GenerateOneToOneRelationFromTarget(CodeTypeDeclaration classDeclaration, CodeNamespace nameSpace,
                                              OneToOneRelation relationship)
        {
            CodeMemberField memberField = GetMemberField(relationship.Target.Name, relationship.Target.Name, Accessor.Private, relationship.TargetAccess);
            classDeclaration.Members.Add(memberField);

            CodeMemberProperty memberProperty;
            if (String.IsNullOrEmpty(relationship.SourceDescription))
                memberProperty = GetMemberProperty(memberField, relationship.Target.Name, true, true, relationship.Target.DoesImplementINotifyPropertyChanged(), relationship.Target.DoesImplementINotifyPropertyChanging(), null);
            else
                memberProperty =
                    GetMemberProperty(memberField, relationship.Target.Name, true, true, relationship.Target.DoesImplementINotifyPropertyChanged(), relationship.Target.DoesImplementINotifyPropertyChanging(),
                                      relationship.SourceDescription);
            classDeclaration.Members.Add(memberProperty);

            memberProperty.CustomAttributes.Add(relationship.GetOneToOneAttributeForSource());
        }

        private void GenerateOneToOneRelationFromSources(CodeTypeDeclaration classDeclaration, CodeNamespace nameSpace,
                                               OneToOneRelation relationship)
        {
            CodeMemberField memberField = GetMemberField(relationship.Source.Name, relationship.Source.Name, Accessor.Private, relationship.SourceAccess);
            classDeclaration.Members.Add(memberField);

            CodeMemberProperty memberProperty;
            if (String.IsNullOrEmpty(relationship.TargetDescription))
                memberProperty = GetMemberProperty(memberField, relationship.Source.Name, true, true, relationship.Source.DoesImplementINotifyPropertyChanged(), relationship.Source.DoesImplementINotifyPropertyChanging(), null);
            else
                memberProperty =
                    GetMemberProperty(memberField, relationship.Source.Name, true, true, relationship.Source.DoesImplementINotifyPropertyChanged(), relationship.Source.DoesImplementINotifyPropertyChanging(),
                                      relationship.TargetDescription);
            classDeclaration.Members.Add(memberProperty);

            memberProperty.CustomAttributes.Add(relationship.GetOneToOneAttributeForTarget());
        }

        #endregion

        #region Nested

        private void GenerateNestingRelationFromRelationship(CodeTypeDeclaration classDeclaration, NestedClassReferencesModelClasses relationship)
        {
            string propertyName = String.IsNullOrEmpty(relationship.PropertyName)
                          ? relationship.NestedClass.Name
                          : relationship.PropertyName;

            CodeMemberField memberField = GetMemberField(propertyName, relationship.NestedClass.Name, Accessor.Private, PropertyAccess.Property);
            classDeclaration.Members.Add(memberField);

            CodeMemberProperty memberProperty;
            if (String.IsNullOrEmpty(relationship.Description))
                memberProperty = GetMemberProperty(memberField, propertyName, true, true, relationship.ModelClass.DoesImplementINotifyPropertyChanged(), relationship.ModelClass.DoesImplementINotifyPropertyChanging(), null);
            else
                memberProperty =
                    GetMemberProperty(memberField, propertyName, true, true, relationship.ModelClass.DoesImplementINotifyPropertyChanged(), relationship.ModelClass.DoesImplementINotifyPropertyChanging(),
                                      relationship.Description);
            classDeclaration.Members.Add(memberProperty);

            memberProperty.CustomAttributes.Add(relationship.GetNestedAttribute());
        }

        #endregion

        #endregion

        #region Comment

        private CodeCommentStatementCollection GetSummaryComment(params string[] comments)
        {
            CodeCommentStatementCollection collection = new CodeCommentStatementCollection();
            if (comments != null && comments.Length > 0)
            {
                for (int i = 0; i < comments.Length; i++)
                    if (!String.IsNullOrEmpty(comments[i]))
                        collection.Add(new CodeCommentStatement(comments[i], true));
            }

            if (collection.Count == 0)
                return collection;

            collection.Insert(0, new CodeCommentStatement("<summary>", true));
            collection.Add(new CodeCommentStatement("</summary>", true));
            return collection;
        }

        #endregion

        #region Code Generation

        private CodeTypeDeclaration GenerateNestedClass(NestedClass cls, CodeNamespace nameSpace)
        {
            if (cls == null)
                throw new ArgumentNullException( "cls", "Nested class not supplied");
            if (nameSpace == null)
                throw new ArgumentNullException("nameSpace", "Namespace not supplied");
            if (String.IsNullOrEmpty(cls.Name))
                throw new ArgumentException("Class name cannot be blank", "cls");

            CodeTypeDeclaration classDeclaration = GetNestedClassDeclaration(cls, nameSpace);

            // Properties and Fields
            foreach (var property in cls.Properties)
            {
                CodeMemberField memberField = GetMemberField(classDeclaration, property);

                classDeclaration.Members.Add(memberField);

                if (property.DebuggerDisplay)
                    classDeclaration.CustomAttributes.Add(property.GetDebuggerDisplayAttribute());
                if (property.DefaultMember)
                    classDeclaration.CustomAttributes.Add(property.GetDefaultMemberAttribute());
            }

            return classDeclaration;
        }

        private CodeTypeDeclaration GenerateClass(ModelClass cls, CodeNamespace nameSpace)
        {
            if (cls == null)
                throw new ArgumentNullException("cls", "Class not supplied");
            if (nameSpace == null)
                throw new ArgumentNullException("nameSpace", "Namespace not supplied");
            if (String.IsNullOrEmpty(cls.Name))
                throw new ArgumentException("Class name cannot be blank", "cls");

            CodeTypeDeclaration classDeclaration = GetClassDeclaration(cls, nameSpace);

            List<ModelProperty> compositeKeys = new List<ModelProperty>();

            // Properties and Fields
            foreach (ModelProperty property in cls.Properties)
            {
                if (property.KeyType != KeyType.CompositeKey)
                {
                    CodeMemberField memberField = GetMemberField(classDeclaration, property);

                    classDeclaration.Members.Add(memberField);

                    if (property.DebuggerDisplay)
                        classDeclaration.CustomAttributes.Add(property.GetDebuggerDisplayAttribute());
                    if (property.DefaultMember)
                        classDeclaration.CustomAttributes.Add(property.GetDefaultMemberAttribute());
                }
                else
                    compositeKeys.Add(property);
            }

            if (compositeKeys.Count > 0)
            {
                CodeTypeDeclaration compositeClass =
                    GetCompositeClassDeclaration(nameSpace, classDeclaration, compositeKeys, cls.DoesImplementINotifyPropertyChanged());

                // TODO: All access fields in a composite group assumed to be the same.
                // We have a model validator for this case but the user may save anyway.
                // Check if all access fields are the same.
                CodeMemberField memberField = GetPrivateMemberFieldOfCompositeClass(compositeClass, PropertyAccess.Property);
                classDeclaration.Members.Add(memberField);

                classDeclaration.Members.Add(GetActiveRecordMemberCompositeKeyProperty(compositeClass, memberField, cls.DoesImplementINotifyPropertyChanged(), cls.DoesImplementINotifyPropertyChanging()));
            }

            //ManyToOne links where this class is the target (1-n)
            ReadOnlyCollection<ManyToOneRelation> manyToOneSources = ManyToOneRelation.GetLinksToSources(cls);
            foreach (ManyToOneRelation relationship in manyToOneSources)
            {
                GenerateHasManyRelation(classDeclaration, nameSpace, relationship);
            }

            //ManyToOne links where this class is the source (n-1)
            ReadOnlyCollection<ManyToOneRelation> manyToOneTargets = ManyToOneRelation.GetLinksToTargets(cls);
            foreach (ManyToOneRelation relationship in manyToOneTargets)
            {
                GenerateBelongsToRelation(classDeclaration, nameSpace, relationship);
            }

            //ManyToMany links where this class is the source
            ReadOnlyCollection<ManyToManyRelation> manyToManyTargets = ManyToManyRelation.GetLinksToManyToManyTargets(cls);
            foreach (ManyToManyRelation relationship in manyToManyTargets)
            {
                GenerateHasAndBelongsToRelationFromTargets(classDeclaration, nameSpace, relationship);
            }

            //ManyToMany links where this class is the target
            ReadOnlyCollection<ManyToManyRelation> manyToManySources = ManyToManyRelation.GetLinksToManyToManySources(cls);
            foreach (ManyToManyRelation relationship in manyToManySources)
            {
                GenerateHasAndBelongsToRelationFromSources(classDeclaration, nameSpace, relationship);
            }

            //OneToOne link where this class is the source
            OneToOneRelation oneToOneTarget = OneToOneRelation.GetLinkToOneToOneTarget(cls);
            if (oneToOneTarget != null)
            {
                GenerateOneToOneRelationFromTarget(classDeclaration, nameSpace, oneToOneTarget);
            }

            //OneToOne links where this class is the target
            ReadOnlyCollection<OneToOneRelation> oneToOneSources = OneToOneRelation.GetLinksToOneToOneSources(cls);
            foreach (OneToOneRelation relationship in oneToOneSources)
            {
                GenerateOneToOneRelationFromSources(classDeclaration, nameSpace, relationship);
            }

            //Nested links
            ReadOnlyCollection<NestedClassReferencesModelClasses> nestingTargets =
                NestedClassReferencesModelClasses.GetLinksToNestedClasses(cls);
            foreach (NestedClassReferencesModelClasses relationship in nestingTargets)
            {
                GenerateNestingRelationFromRelationship(classDeclaration, relationship);
            }

            // TODO: Other relation types (any etc)

            GenerateDerivedClass(cls, nameSpace);

            return classDeclaration;
        }

        public static CodeExpression GetBooleanAnd(List<CodeExpression> expressions, int i)
        {
            if (i == expressions.Count - 2)
                return
                    new CodeBinaryOperatorExpression(expressions[i], CodeBinaryOperatorType.BooleanAnd,
                                                     expressions[i + 1]);
            
            return
                new CodeBinaryOperatorExpression(expressions[i], CodeBinaryOperatorType.BooleanAnd,
                                                 GetBooleanAnd(expressions, i + 1));
        }

        public static CodeExpression GetBinaryOr(ArrayList list, int indexOfLeft)
        {
            if (indexOfLeft + 1 == list.Count)
                return (CodeExpression)list[0];
            
            if (indexOfLeft + 2 == list.Count)
                return
                    new CodeBinaryOperatorExpression((CodeExpression)list[indexOfLeft],
                                                     CodeBinaryOperatorType.BitwiseOr,
                                                     (CodeExpression)list[indexOfLeft + 1]);
            
            return
                new CodeBinaryOperatorExpression((CodeExpression)list[indexOfLeft],
                                                 CodeBinaryOperatorType.BitwiseOr,
                                                 GetBinaryOr(list, indexOfLeft + 1));
        }

        private static CodeExpression GetXor(IList<CodeExpression> expressions, int i)
        {
            if (i == expressions.Count - 2)
                return new CodeMethodInvokeExpression(null, Common.XorHelperMethod, expressions[i], expressions[i + 1]);
            
            return
                new CodeMethodInvokeExpression(null, Common.XorHelperMethod, expressions[i],
                                               GetXor(expressions, i + 1));
        }

        private string GenerateCode(CodeCompileUnit compileUnit)
        {
            StringBuilder sb = new StringBuilder();
            using (StringWriter sw = new StringWriter(sb))
            {
                Context.Provider.GenerateCodeFromCompileUnit(compileUnit, sw, new CodeGeneratorOptions());
            }

            return sb.ToString();
        }

        private void UseNHQG(Assembly assembly)
        {
            System.Diagnostics.Process nhqg = new System.Diagnostics.Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();

            string tempFilePath = Path.Combine(DTEHelper.GetIntermediatePath(DTEHelper.GetVSProject(Context.ProjectItem)), "ActiveWriterHbmOutput");
            DirectoryInfo tempFileFolder = null;

            try
            {
                if (Directory.Exists(tempFilePath))
                {
                    tempFileFolder = new DirectoryInfo(tempFilePath);
                    foreach (var file in tempFileFolder.GetFiles())
                    {
                        file.Delete();
                    }
                }
                else
                    tempFileFolder = Directory.CreateDirectory(tempFilePath);

                startInfo.FileName = Context.Model.NHQGExecutable;
                startInfo.WorkingDirectory = Path.GetDirectoryName(Context.Model.NHQGExecutable);
                string[] args = new string[4];
                args[0] = "/lang:" + (Context.Language == CodeLanguage.CSharp ? "CS" : "VB");
                args[1] = "/files:\"" + assembly.Location + "\"";
                args[2] = "/out:\"" + tempFileFolder.FullName + "\"";
                args[3] = "/ns:" + Context.Namespace;
                startInfo.Arguments = string.Join(" ", args);
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.ErrorDialog = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.UseShellExecute = false;
                nhqg.StartInfo = startInfo;

                Log("Running NHQG with parameters: " + String.Join(" ", args));

                nhqg.Start();
                StreamReader output = nhqg.StandardOutput;
                nhqg.WaitForExit(); // Timeout?

                if (nhqg.ExitCode != 0)
                {
                    throw new TargetException("NHQG exited with code " + nhqg.ExitCode);
                }
                else
                {
                    string consoleOut = output.ReadToEnd();
                    if (!string.IsNullOrEmpty(consoleOut) && consoleOut.StartsWith("An error occured:"))
                    {
                        throw new TargetException("NHQG exited with the following error:\n\n" + consoleOut);
                    }
                    else
                    {
                        foreach (var file in tempFileFolder.GetFiles())
                        {
                            string filePath = Path.Combine(Context.ModelFilePath, file.Name);
                            file.CopyTo(filePath , true);
                            AddToProject(filePath, prjBuildAction.prjBuildActionCompile);
                        }
                    }
                }
            }
            finally
            {
                if (tempFileFolder != null)
                    tempFileFolder.Delete(true);
            }
        }

        private void AddToProject(string path, prjBuildAction buildAction)
        {
            ProjectItem item = null;

            if (Context.Model.RelateWithActiwFile)
                item = Context.ProjectItem.ProjectItems.AddFromFile(path);
            else
                item = Context.DTE.ItemOperations.AddExistingItem(path);

            item.Properties.Item("BuildAction").Value = (int)buildAction;
        }

        private Assembly GenerateARAssembly(CodeCompileUnit compileUnit, bool generateInMemory)
        {
            List<string> addedAssemblies = new List<string>();
            string assemblyName = "ActiveWriter.Temp.Assembly" + Guid.NewGuid().ToString("N") + ".dll";
            CompilerParameters parameters = new CompilerParameters
                                                {
                                                    GenerateInMemory = generateInMemory,
                                                    GenerateExecutable = false
                                                };

            Assembly activeRecord = Assembly.Load(Context.Model.ActiveRecordAssemblyName);
            parameters.ReferencedAssemblies.Add(activeRecord.Location);
            Assembly nHibernate = Assembly.Load(Context.Model.NHibernateAssemblyName);
            parameters.ReferencedAssemblies.Add(nHibernate.Location);
            parameters.ReferencedAssemblies.Add("System.dll");
            parameters.ReferencedAssemblies.Add("mscorlib.dll");
            addedAssemblies.Add("castle.activerecord.dll");
            addedAssemblies.Add("nhibernate.dll");
            addedAssemblies.Add("system.dll");
            addedAssemblies.Add("mscorlib.dll");

            // also add references to assemblies referenced by this project
            VSProject proj = DTEHelper.GetVSProject(Context.ProjectItem);
            foreach (Reference reference in proj.References)
            {
                if (!addedAssemblies.Contains(Path.GetFileName(reference.Path).ToLowerInvariant()))
                {
                    parameters.ReferencedAssemblies.Add(reference.Path);
                    addedAssemblies.Add(Path.GetFileName(reference.Path).ToLowerInvariant());
                }
            }

            parameters.OutputAssembly = generateInMemory
                                            ? assemblyName
                                            : Path.Combine(
                                                  DTEHelper.GetIntermediatePath(proj), assemblyName);
            
            CompilerResults results = Context.Provider.CompileAssemblyFromDom(parameters, compileUnit);
            if (results.Errors.Count == 0)
            {
                return results.CompiledAssembly;
            }
            
            Context.TextTemplatingHost.LogErrors(results.Errors);
            throw new ModelingException("Cannot compile in-memory ActiveRecord assembly due to errors. Please check that all the information required, such as imports, to compile the generated code in-memory is provided. An ActiveRecord assembly is generated in-memory to support NHibernate .hbm.xml generation and NHQG integration.");
        }

        // Actually: OnARModelCreated(ActiveRecordModelCollection, IConfigurationSource)
        // We're not using it typed since we use this through reflection.
        public void OnARModelCreated(object models, object source)
        {
            _nHibernateConfigs.Clear();
            if (models != null)
            {
                Type modelCollection = _activeRecord.GetType("Castle.ActiveRecord.Framework.Internal.ActiveRecordModelCollection");
                if ((int)modelCollection.GetProperty("Count").GetValue(models, null) > 0)
                {
                    string actualAssemblyName = ", " + DTEHelper.GetAssemblyName(Context.ProjectItem.ContainingProject);
                    IEnumerator enumerator = (IEnumerator)modelCollection.InvokeMember("GetEnumerator", BindingFlags.Public | BindingFlags.InvokeMethod | BindingFlags.Instance, null, models, null);
                    while (enumerator.MoveNext())
                    {
                        Type visitor = _activeRecord.GetType("Castle.ActiveRecord.Framework.Internal.XmlGenerationVisitor");
                        object generationVisitor = Activator.CreateInstance(visitor);
                        visitor.InvokeMember("CreateXml", BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod, null,
                                             generationVisitor, new object[] { enumerator.Current });

                        string xml = (string)visitor.GetProperty("Xml").GetValue(generationVisitor, null);
                        XmlDocument document = new XmlDocument();
                        document.PreserveWhitespace = true;
                        document.LoadXml(xml);
                        XmlNodeList nodeList = document.GetElementsByTagName("class");
                        if (nodeList.Count > 0)
                        {
                            XmlAttribute attribute = (XmlAttribute) nodeList[0].Attributes.GetNamedItem("name");



                            if (attribute != null)
                            {
                                string tempAssemblyName = attribute.Value.Substring(attribute.Value.LastIndexOf(','));

                                string name = attribute.Value;

                                name = name.Substring(0, name.LastIndexOf(','));

                                string newValue = name;
                                // if name isn't a fully-qualified namespace, then prepend project default
                                if ((name.IndexOf('.') < 0) && !string.IsNullOrEmpty(Context.DefaultNamespace))
                                    newValue = Context.DefaultNamespace + "." + name;

                                // append assembly name
                                attribute.Value = newValue + actualAssemblyName;

                                // also fix any class attributes
                                XmlNodeList ClassAttributes = document.SelectNodes("//@class");

                                foreach (XmlAttribute ClassAttribute in ClassAttributes)
                                {
                                    UpdateClassName(actualAssemblyName, tempAssemblyName, ClassAttribute);
                                }

                                xml = document.OuterXml;
                                _nHibernateConfigs.Add(name, xml);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Fix class attributes that were referencing a temporary assembly name by prefixing with class namespace and using
        /// correct assembly name
        /// </summary>
        /// <param name="actualAssemblyName"></param>
        /// <param name="tempAssemblyName"></param>
        /// <param name="attribute"></param>
        private void UpdateClassName(string actualAssemblyName, string tempAssemblyName, XmlAttribute attribute)
        {
            //remove temporary assembly name
            string name = attribute.Value.Replace(tempAssemblyName, String.Empty);

            if (attribute.Value.Length != name.Length) {

                // if name isn't a fully-qualified namespace, then prepend project default
                if ((name.IndexOf('.') < 0) && !string.IsNullOrEmpty(Context.DefaultNamespace))
                    name = Context.DefaultNamespace + "." + name;

                // append assembly name
                attribute.Value = name + actualAssemblyName;
            }
        }

        private Assembly AssemblyResolve(object sender, ResolveEventArgs args)
        {
            AssemblyName name = new AssemblyName(args.Name);
            if (name.Name == "Castle.ActiveRecord" || name.Name == "Iesi.Collections" || name.Name == "log4net" || name.Name == "NHibernate")
            {
                // If this line is reached, these assemblies are not in GAC. Load from designated place.
                string assemblyPath = Path.Combine(Context.Model.AssemblyPath, name.Name + ".dll");

                // try project references
                if (!File.Exists(assemblyPath))
                {
                    VSProject proj = (VSProject)Context.ProjectItem.ContainingProject.Object;
                    foreach (Reference reference in proj.References)
                    {
                        if (reference.Name == name.Name)
                        {
                            assemblyPath = reference.Path;
                            break;
                        }
                    }

                }

                return Assembly.LoadFrom(assemblyPath);
            }

            return null;
        }

        private void ClearARAttributes(CodeCompileUnit unit)
        {
            foreach (CodeNamespace ns in unit.Namespaces)
            {
                List<CodeNamespaceImport> imports = new List<CodeNamespaceImport>();
                foreach (CodeNamespaceImport import in ns.Imports)
                {
                    if (!(import.Namespace.IndexOf("Castle") > -1 || import.Namespace.IndexOf("Nullables") > -1))
                        imports.Add(import);
                }
                ns.Imports.Clear();
                ns.Imports.AddRange(imports.ToArray());

                foreach (CodeTypeDeclaration type in ns.Types)
                {
                    List<CodeAttributeDeclaration> attributesToRemove = new List<CodeAttributeDeclaration>();
                    foreach (CodeAttributeDeclaration attribute in type.CustomAttributes)
                    {
                        if (Array.FindIndex(Common.ARAttributes, name => name == attribute.Name) > -1)
                            attributesToRemove.Add(attribute);
                    }
                    foreach (CodeAttributeDeclaration declaration in attributesToRemove)
                    {
                        type.CustomAttributes.Remove(declaration);
                    }


                    foreach (CodeTypeMember member in type.Members)
                    {
                        List<CodeAttributeDeclaration> memberAttributesToRemove = new List<CodeAttributeDeclaration>();
                        foreach (CodeAttributeDeclaration attribute in member.CustomAttributes)
                        {
                            if (Array.FindIndex(Common.ARAttributes, name => name == attribute.Name) > -1)
                                memberAttributesToRemove.Add(attribute);
                        }
                        foreach (CodeAttributeDeclaration declaration in memberAttributesToRemove)
                        {
                            member.CustomAttributes.Remove(declaration);
                        }
                    }
                }
            }
        }

        private string RemoveNamespaceFromStart(string name)
        {
            if (!string.IsNullOrEmpty(Context.Namespace) && name.StartsWith(Context.Namespace))
                return name.Remove(0, Context.Namespace.Length + 1);

            return name;
        }

        private void AddINotifyPropertyChangedRegion(CodeTypeDeclaration declaration)
        {
            CodeMemberEvent memberEvent = new CodeMemberEvent
                                              {
                                                  Attributes = MemberAttributes.Public,
                                                  Name = "PropertyChanged",
                                                  Type = new CodeTypeReference("PropertyChangedEventHandler")
                                              };
            memberEvent.StartDirectives.Add(new CodeRegionDirective(CodeRegionMode.Start, "INotifyPropertyChanged Members"));
            declaration.Members.Add(memberEvent);

            CodeMemberMethod propertyChangedMethod = new CodeMemberMethod
                                                         {
                                                             Attributes = MemberAttributes.Family,
                                                             Name = Common.PropertyChangedInternalMethod
                                                         };
            propertyChangedMethod.EndDirectives.Add(new CodeRegionDirective(CodeRegionMode.End, null));
            propertyChangedMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof (string), "information"));
            declaration.Members.Add(propertyChangedMethod);

            CodeConditionStatement ifStatement = new CodeConditionStatement(
                new CodeBinaryOperatorExpression(
                    new CodeFieldReferenceExpression(null, "PropertyChanged"),
                    CodeBinaryOperatorType.IdentityInequality, new CodePrimitiveExpression(null)
                    ), new CodeExpressionStatement(
                new CodeMethodInvokeExpression(null, "PropertyChanged",
                                               new CodeThisReferenceExpression(),
                                               new CodeObjectCreateExpression(
                                                   "PropertyChangedEventArgs",
                                                   new CodeFieldReferenceExpression(null, "information"))))
                );
            propertyChangedMethod.Statements.Add(ifStatement);
        }

        private void AddINotifyPropertyChangingRegion(CodeTypeDeclaration declaration)
        {
            CodeMemberEvent memberEvent = new CodeMemberEvent
                                              {
                                                  Attributes = MemberAttributes.Public,
                                                  Name = "PropertyChanging",
                                                  Type = new CodeTypeReference("PropertyChangingEventHandler")
                                              };
            memberEvent.StartDirectives.Add(new CodeRegionDirective(CodeRegionMode.Start, "INotifyPropertyChanging Members"));
            declaration.Members.Add(memberEvent);

            CodeMemberMethod propertyChangingMethod = new CodeMemberMethod
                                                          {
                                                              Attributes = MemberAttributes.Family,
                                                              Name = Common.PropertyChangingInternalMethod
                                                          };
            propertyChangingMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof(string), "information"));
            propertyChangingMethod.EndDirectives.Add(new CodeRegionDirective(CodeRegionMode.End, null));
            declaration.Members.Add(propertyChangingMethod);

            CodeConditionStatement ifStatement = new CodeConditionStatement(
                new CodeBinaryOperatorExpression(
                    new CodeFieldReferenceExpression(null, "PropertyChanging"),
                    CodeBinaryOperatorType.IdentityInequality, new CodePrimitiveExpression(null)
                    ), new CodeExpressionStatement(
                new CodeMethodInvokeExpression(null, "PropertyChanging",
                                               new CodeThisReferenceExpression(),
                                               new CodeObjectCreateExpression(
                                                   "PropertyChangingEventArgs",
                                                   new CodeFieldReferenceExpression(null, "information"))))
                );
            propertyChangingMethod.Statements.Add(ifStatement);
        }

        private void GenerateMetaData(CodeNamespace nameSpace)
        {
            foreach (CodeTypeDeclaration type in nameSpace.Types)
            {
                List<CodeTypeMember> properties = new List<CodeTypeMember>();
                foreach (CodeTypeMember member in type.Members)
                {
                    if ((member is CodeMemberProperty || member is CodeMemberField) && ModelProperty.IsMetaDataGeneratable(member))
                    {
                        properties.Add(member);
                    }
                }

                if (properties.Count > 0)
                {
                    if (Context.Model.GenerateMetaData == MetaDataGeneration.InClass)
                    {
                        GenerateInClassMetaData(type, properties);
                    }
                    else if (Context.Model.GenerateMetaData == MetaDataGeneration.InSubClass)
                    {
                        GenerateSubClassMetaData(type, properties);
                    }
                }
            }
        }

        private void GenerateInClassMetaData(CodeTypeDeclaration type, IEnumerable<CodeTypeMember> members)
        {
            foreach (CodeTypeMember member in members)
            {
                type.Members.Add(GenerateMetaDataField(member, member.Name + "Property"));
            }
        }

        private void GenerateSubClassMetaData(CodeTypeDeclaration type, IEnumerable<CodeTypeMember> members)
        {
            CodeTypeDeclaration subClass = CreateClass("Properties");
            foreach (var member in members)
            {
                subClass.Members.Add(GenerateMetaDataField(member, member.Name));
            }

            type.Members.Add(subClass);
        }

        private CodeMemberField GenerateMetaDataField(CodeTypeMember member, string nm)
        {
            CodeMemberField field = GetStaticMemberFieldWithoutTypeAndName(Accessor.Public);
            field.Name = NamingHelper.GetName(nm, PropertyAccess.Property, FieldCase.Pascalcase);
            field.Type = new CodeTypeReference(typeof(string));
            field.InitExpression = new CodePrimitiveExpression(member.Name);
            return field;
        }

        private void Log(string message)
        {
            Context.Output.Write(string.Format("ActiveWriter: {0}", message));
        }

        private CodeTypeReference GetConcreteListType(string sourceClassName)
        {
            CodeTypeReference fieldCreatorType = new CodeTypeReference(Context.Model.EffectiveListClass);
            fieldCreatorType.TypeArguments.Add(sourceClassName);
            return fieldCreatorType;
        }

        private void GenerateHelperClass(CodeNamespace nameSpace)
        {
            if (Context.Model.Target == CodeGenerationTarget.ActiveRecord)
            {
                CodeTypeDeclaration helperClass = new CodeTypeDeclaration(Context.HelperClassName);
                nameSpace.Types.Add(helperClass);
                CodeMemberMethod getTypesMethod = new CodeMemberMethod();
                helperClass.Members.Add(getTypesMethod);
                getTypesMethod.Name = "GetTypes";
                getTypesMethod.ReturnType = new CodeTypeReference("Type", 1);
                getTypesMethod.Attributes = MemberAttributes.Public | MemberAttributes.Static;

                CodeArrayCreateExpression arrayCreate = new CodeArrayCreateExpression("Type");
                foreach (ModelClass modelClass in Context.Model.Classes)
                    arrayCreate.Initializers.Add(new CodeTypeOfExpression(modelClass.Name));

                getTypesMethod.Statements.Add(new CodeMethodReturnStatement(arrayCreate));
            }
        }

        private void GenerateDerivedClass(ModelClass cls, CodeNamespace nameSpace)
        {
            if (cls.Model.GeneratesDoubleDerived)
            {
                CodeTypeDeclaration derivedClass = new CodeTypeDeclaration(cls.Name);
                derivedClass.IsPartial = true;
                derivedClass.TypeAttributes = TypeAttributes.Public;
                derivedClass.BaseTypes.Add(new CodeTypeReference(cls.Name + cls.Model.DoubleDerivedNameSuffix));
                derivedClass.CustomAttributes.Add(cls.GetActiveRecordAttribute());

                nameSpace.Types.Add(derivedClass);
            }
        }

        #endregion

        #endregion
    }
}
