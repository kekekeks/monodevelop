// 
// SyntaxMode.cs
//  
// Author:
//   Mike Krüger <mkrueger@novell.com>
//
// Copyright (C) 2009 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Linq;
using System.Collections.Generic;
using Mono.TextEditor.Highlighting;
using Mono.TextEditor;
using System.Xml;
using MonoDevelop.Projects;
using MonoDevelop.CSharp.Project;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Tasks;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.TypeSystem;
using MonoDevelop.Ide.TypeSystem;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.CSharp.TypeSystem;
using MonoDevelop.SourceEditor.QuickTasks;
using System.Threading;
using System.Diagnostics;
using MonoDevelop.Core;


namespace MonoDevelop.CSharp.Highlighting
{
	static class StringHelper
	{
		public static bool IsAt (this string str, int idx, string pattern)
		{
			if (idx + pattern.Length > str.Length)
				return false;

			for (int i = 0; i < pattern.Length; i++)
				if (pattern [i] != str [idx + i])
					return false;
			return true;
		}
	}
	
	class CSharpSyntaxMode : SyntaxMode, IQuickTaskProvider, IDisposable
	{
		readonly Document guiDocument;

		SyntaxTree unit;
		CSharpUnresolvedFile parsedFile;
		ICompilation compilation;
		CSharpAstResolver resolver;
		CancellationTokenSource src;

		public bool SemanticHighlightingEnabled {
			get;
			set;
		}

		internal class StyledTreeSegment : TreeSegment
		{
			public string Style {
				get;
				private set;
			}
			
			public StyledTreeSegment (int offset, int length, string style) : base (offset, length)
			{
				Style = style;
			}
		}
		
		class HighlightingSegmentTree : SegmentTree<StyledTreeSegment>
		{
			public bool GetStyle (Chunk chunk, ref int endOffset, out string style)
			{
				var segment = GetSegmentsAt (chunk.Offset).FirstOrDefault ();
				if (segment == null) {
					style = null;
					return false;
				}
				endOffset = segment.EndOffset;
				style = segment.Style;
				return true;
			}
			
			public void AddStyle (int startOffset, int endOffset, string style)
			{
				if (IsDirty)
					return;
				Add (new StyledTreeSegment (startOffset, endOffset - startOffset, style));
			}
		}
		
		Dictionary<DocumentLine, HighlightingVisitior> lineSegments = new Dictionary<DocumentLine, HighlightingVisitior> ();

		public bool DisableConditionalHighlighting {
			get;
			set;
		}

		void HandleDocumentParsed (object sender, EventArgs e)
		{
			if (src != null)
				src.Cancel ();
			resolver = null;
			if (guiDocument != null && SemanticHighlightingEnabled) {
				var parsedDocument = guiDocument.ParsedDocument;
				if (parsedDocument != null) {
					unit = parsedDocument.GetAst<SyntaxTree> ();
					parsedFile = parsedDocument.ParsedFile as CSharpUnresolvedFile;
					if (guiDocument.Project != null && guiDocument.IsCompileableInProject) {
						src = new CancellationTokenSource ();
						var cancellationToken = src.Token;
						compilation = guiDocument.Compilation;
						var newResolver = new CSharpAstResolver (compilation, unit, parsedFile);
						System.Threading.Tasks.Task.Factory.StartNew (delegate {
							var visitor = new QuickTaskVisitor (newResolver, cancellationToken);
							try {
								unit.AcceptVisitor (visitor);
							} catch (Exception ex) {
								LoggingService.LogError ("Error while analyzing the file for the semantic highlighting.", ex);
								return;
							}
							if (!cancellationToken.IsCancellationRequested) {
								Gtk.Application.Invoke (delegate {
									if (cancellationToken.IsCancellationRequested)
										return;
									var editorData = guiDocument.Editor;
									if (editorData == null)
										return;
									resolver = newResolver;
									quickTasks = visitor.QuickTasks;
									OnTasksUpdated (EventArgs.Empty);
									foreach (var kv in lineSegments) {
										try {
											kv.Value.tree.RemoveListener ();
										} catch (Exception) {
										}
									}
									lineSegments.Clear ();
									var textEditor = editorData.Parent;
									if (textEditor != null) {
										if (!parsedDocument.HasErrors) {
											var margin = textEditor.TextViewMargin;
											margin.PurgeLayoutCache ();
											textEditor.QueueDraw ();
										}
									}
								});
							}
						}, cancellationToken);
					}
				}
			}
		}

		class HighlightingVisitior : DepthFirstAstVisitor
		{
			readonly CSharpAstResolver resolver;
			readonly CancellationToken cancellationToken;
			readonly int lineNumber;
			readonly int lineOffset;
			internal HighlightingSegmentTree tree = new HighlightingSegmentTree ();

			public HighlightingVisitior (CSharpAstResolver resolver, CancellationToken cancellationToken, int lineNumber, int lineOffset)
			{
				if (resolver == null)
					throw new ArgumentNullException ("resolver");
				this.resolver = resolver;
				this.cancellationToken = cancellationToken;
				this.lineNumber = lineNumber;
				this.lineOffset = lineOffset;
			}

			void Colorize (AstNode node, string style)
			{
				var start = lineOffset + node.StartLocation.Column - 1;
				var end   = lineOffset + node.EndLocation.Column - 1;
				tree.AddStyle (start, end, style);
			}

			public override void VisitCSharpTokenNode (CSharpTokenNode token)
			{
				if (token.StartLocation.Line != lineNumber)
					return;
				var mod = token as CSharpModifierToken;
				if (mod != null && mod.Modifier == Modifiers.Partial)
					Colorize (token, contextualHighlightKeywords["partial"]);
			}

			protected override void VisitChildren (AstNode node)
			{
				for (var child = node.FirstChild; child != null; child = child.NextSibling) {
					if (child.StartLocation.Line <= lineNumber && child.EndLocation.Line >= lineNumber)
						child.AcceptVisitor(this);
				}
			}

			public override void VisitConstraint (Constraint constraint)
			{
				base.VisitConstraint (constraint);
				if (constraint.WhereKeyword.StartLocation.Line == lineNumber)
					Colorize (constraint.WhereKeyword, contextualHighlightKeywords["where"]);
			}

			public override void VisitIdentifierExpression (IdentifierExpression identifierExpression)
			{
				foreach (var tp in identifierExpression.TypeArguments) {
					tp.AcceptVisitor (this);
				}
				if (identifierExpression.StartLocation.Line != lineNumber)
					return;
				if (isInAccessor && identifierExpression.Identifier == "value") {
					Colorize (identifierExpression, contextualHighlightKeywords["value"]);
					return;
				}

				var result = resolver.Resolve (identifierExpression, cancellationToken);
				if (result.IsError) {
					Colorize (identifierExpression, "Syntax Error");
					return;
				}

				if (result is MemberResolveResult) {
					var member = ((MemberResolveResult)result).Member;
					switch (member.EntityType) {
					case EntityType.Field:
						Colorize (identifierExpression.IdentifierToken, "User Field Usage");
						break;
					case EntityType.Property:
						Colorize (identifierExpression.IdentifierToken, "User Property Usage");
						break;
					case EntityType.Method:
						Colorize (identifierExpression.IdentifierToken, "User Method Usage");
						break;
					case EntityType.Event:
						Colorize (identifierExpression.IdentifierToken, "User Event Usage");
						break;
					}
					return;
				}

				if (result is MethodGroupResolveResult) {
					Colorize (identifierExpression.IdentifierToken, "User Method Usage");
					return;
				}

				if (result is TypeResolveResult) {
					Colorize (identifierExpression.IdentifierToken, GetUserTypeHighlighting (result.Type));
					return;
				}

				var localResult = result as LocalResolveResult;
				if (localResult != null) {
					if (localResult.Variable is IParameter) {
						Colorize (identifierExpression.IdentifierToken, "User Parameter Usage");
					} else {
						Colorize (identifierExpression.IdentifierToken, "User Variable Usage");
					}
				}
			}

			bool isInAccessor;
			public override void VisitAccessor(Accessor accessor)
			{
				isInAccessor = true;
				try {
					base.VisitAccessor(accessor);
				} finally {
					isInAccessor = false;
				}
			}
			public override void VisitExternAliasDeclaration (ExternAliasDeclaration externAliasDeclaration)
			{
				base.VisitExternAliasDeclaration (externAliasDeclaration);
				if (externAliasDeclaration.AliasToken.StartLocation.Line == lineNumber)
					Colorize (externAliasDeclaration.AliasToken, "Keyword(Namespace)");
			}
			Stack<TypeDeclaration> typeDeclarations = new Stack<TypeDeclaration> ();
			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				typeDeclarations.Push (typeDeclaration);
				base.VisitTypeDeclaration (typeDeclaration);
				typeDeclarations.Pop ();

				if (typeDeclaration.NameToken.StartLocation.Line == lineNumber)
					Colorize (typeDeclaration.NameToken, GetUserTypeHighlighting (typeDeclaration.ClassType));
			}

			public override void VisitPropertyDeclaration (PropertyDeclaration propertyDeclaration)
			{
				base.VisitPropertyDeclaration (propertyDeclaration);
				if (propertyDeclaration.NameToken.StartLocation.Line == lineNumber) {
					if (!propertyDeclaration.PrivateImplementationType.IsNull) {
						if (!CheckInterfaceImplementation (propertyDeclaration))
							return;
					}
					Colorize (propertyDeclaration.NameToken, "User Property Declaration");
				}
				if (!propertyDeclaration.Getter.IsNull) {
					var getKeyword = propertyDeclaration.Getter.GetChildByRole (PropertyDeclaration.GetKeywordRole);
					if (getKeyword != null && getKeyword.StartLocation.Line == lineNumber)
						Colorize (getKeyword, contextualHighlightKeywords ["get"]);
				}
				if (!propertyDeclaration.Setter.IsNull) {
					var setKeyword = propertyDeclaration.Setter.GetChildByRole (PropertyDeclaration.SetKeywordRole);
					if (setKeyword != null &&setKeyword.StartLocation.Line == lineNumber)
						Colorize (setKeyword, contextualHighlightKeywords ["set"]);
				}
			}

			public override void VisitArrayInitializerExpression (ArrayInitializerExpression arrayInitializerExpression)
			{
				foreach (var a in arrayInitializerExpression.Elements) {
					var namedElement = a as NamedExpression;
					if (namedElement != null) {
						if (namedElement.NameToken.StartLocation.Line == lineNumber) {
							var result = resolver.Resolve (namedElement, cancellationToken);
							if (result.IsError)
								Colorize (namedElement.NameToken, "Syntax Error");
						}
						namedElement.Expression.AcceptVisitor (this);
					} else {
						a.AcceptVisitor (this);
					}
				}
			}

			public override void VisitEventDeclaration (EventDeclaration eventDeclaration)
			{
				base.VisitEventDeclaration (eventDeclaration);
				foreach (var init in eventDeclaration.Variables)
					if (init.NameToken.StartLocation.Line == lineNumber) {
						Colorize (init.NameToken, "User Event Declaration");
					}
			}

			public override void VisitCustomEventDeclaration (CustomEventDeclaration eventDeclaration)
			{
				base.VisitCustomEventDeclaration (eventDeclaration);
				if (eventDeclaration.NameToken.StartLocation.Line == lineNumber) {
					if (!eventDeclaration.PrivateImplementationType.IsNull) {
						if (!CheckInterfaceImplementation (eventDeclaration))
							return;
					}
					Colorize (eventDeclaration.NameToken, "User Event Declaration");
				}

				if (!eventDeclaration.AddAccessor.IsNull) {
					var addKeyword = eventDeclaration.AddAccessor.GetChildByRole (CustomEventDeclaration.AddKeywordRole);
					if (addKeyword != null && addKeyword.StartLocation.Line == lineNumber)
						Colorize (addKeyword, contextualHighlightKeywords ["add"]);
				}
				if (!eventDeclaration.RemoveAccessor.IsNull) {
					var removeKeyword = eventDeclaration.RemoveAccessor.GetChildByRole (CustomEventDeclaration.RemoveKeywordRole);
					if (removeKeyword != null && removeKeyword.StartLocation.Line == lineNumber)
						Colorize (removeKeyword, contextualHighlightKeywords ["remove"]);
				}
			}

			public override void VisitDelegateDeclaration (DelegateDeclaration delegateDeclaration)
			{
				base.VisitDelegateDeclaration (delegateDeclaration);
				if (delegateDeclaration.NameToken.StartLocation.Line == lineNumber)
					Colorize (delegateDeclaration.NameToken, "User Types(Delegates)");
			}

			public override void VisitParameterDeclaration (ParameterDeclaration parameterDeclaration)
			{
				base.VisitParameterDeclaration (parameterDeclaration);
				if (parameterDeclaration.NameToken.StartLocation.Line == lineNumber)
					Colorize (parameterDeclaration.NameToken, "User Parameter Declaration");
			}

			public override void VisitVariableInitializer (VariableInitializer variableInitializer)
			{
				base.VisitVariableInitializer (variableInitializer);
				if (variableInitializer.NameToken.StartLocation.Line == lineNumber)
					Colorize (variableInitializer.NameToken, "User Variable Declaration");
			}

			public override void VisitTypeParameterDeclaration (TypeParameterDeclaration typeParameterDeclaration)
			{
				base.VisitTypeParameterDeclaration (typeParameterDeclaration);
				if (typeParameterDeclaration.NameToken.StartLocation.Line == lineNumber)
					Colorize (typeParameterDeclaration.NameToken, "User Types(Type parameters)");
			}

			public override void VisitConstructorDeclaration (ConstructorDeclaration constructorDeclaration)
			{
				base.VisitConstructorDeclaration (constructorDeclaration);
				if (constructorDeclaration.NameToken.StartLocation.Line == lineNumber)
					Colorize (constructorDeclaration.NameToken, GetUserTypeHighlighting (this.typeDeclarations.Peek ().ClassType));
			}

			public override void VisitDestructorDeclaration (DestructorDeclaration destructorDeclaration)
			{
				base.VisitDestructorDeclaration (destructorDeclaration);
				if (destructorDeclaration.NameToken.StartLocation.Line == lineNumber)
					Colorize (destructorDeclaration.NameToken, GetUserTypeHighlighting (this.typeDeclarations.Peek ().ClassType));
			}

			bool CheckInterfaceImplementation (EntityDeclaration entityDeclaration)
			{
				var result = resolver.Resolve (entityDeclaration, cancellationToken) as MemberResolveResult;
				if (result.Member.ImplementedInterfaceMembers.Count == 0) {
					Colorize (entityDeclaration.NameToken, "Syntax Error");
					return false;
				}
				return true;
			}

			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);
				if (methodDeclaration.NameToken.StartLocation.Line == lineNumber) {
					if (!methodDeclaration.PrivateImplementationType.IsNull) {
						if (!CheckInterfaceImplementation (methodDeclaration))
							return;
					}
					Colorize (methodDeclaration.NameToken, "User Method Declaration");
				}
			}

			public override void VisitFieldDeclaration (FieldDeclaration fieldDeclaration)
			{
				fieldDeclaration.ReturnType.AcceptVisitor (this);
				foreach (var init in fieldDeclaration.Variables) {
					if (init.NameToken.StartLocation.Line == lineNumber)
						Colorize (init.NameToken, "User Field Declaration");
					init.Initializer.AcceptVisitor (this);
				}
			}

			public override void VisitFixedFieldDeclaration (FixedFieldDeclaration fixedFieldDeclaration)
			{
				base.VisitFixedFieldDeclaration (fixedFieldDeclaration);
				foreach (var init in fixedFieldDeclaration.Variables)
					if (init.NameToken.StartLocation.Line == lineNumber)
						Colorize (init.NameToken, "User Field Declaration");
			}

			public override void VisitUsingDeclaration (UsingDeclaration usingDeclaration)
			{
			}

			public override void VisitUsingAliasDeclaration (UsingAliasDeclaration usingDeclaration)
			{
			}

			public override void VisitComposedType (ComposedType composedType)
			{
				if (composedType.StartLocation.Line != lineNumber) {
					base.VisitComposedType (composedType);
					return;
				}
				var result = resolver.Resolve (composedType, cancellationToken);
				if (result.IsError) {
					// if csharpSyntaxMode.guiDocument.Project != null
					Colorize (composedType, "Syntax Error");
					return;
				}
				if (result is TypeResolveResult) {
					Colorize (composedType.BaseType, GetUserTypeHighlighting(result.Type));
				}

			}

			static string GetUserTypeHighlighting (IType type)
			{
				switch (type.Kind) {
				case TypeKind.Class:
					return "User Types";
				case TypeKind.Struct:
					return "User Types(Value types)";
				case TypeKind.Interface:
					return "User Types(Interfaces)";
				case TypeKind.Delegate:
					return "User Types(Delegates)";
				case TypeKind.Enum:
					return "User Types(Enums)";
				case TypeKind.TypeParameter:
					return "User Types(Type parameters)";
				}
				return "User Types";
			}

			static string GetUserTypeHighlighting (ClassType classType)
			{
				switch (classType) {
				case ClassType.Class:
					return "User Types";
				case ClassType.Struct:
					return "User Types(Value types)";
				case ClassType.Interface:
					return "User Types(Interfaces)";
				case ClassType.Enum:
					return "User Types(Enums)";
				}
				return "User Types";
			}

			public override void VisitSimpleType (SimpleType simpleType)
			{
				if (simpleType.StartLocation.Line != lineNumber) {
					base.VisitSimpleType (simpleType);
					return;
				}
				var result = resolver.Resolve (simpleType, cancellationToken);
				if (result.IsError) {
					// if csharpSyntaxMode.guiDocument.Project != null
					Colorize (simpleType, "Syntax Error");
					return;
				}
				if (result is TypeResolveResult) {
					Colorize (simpleType.IdentifierToken, GetUserTypeHighlighting (result.Type));
				}
				base.VisitSimpleType (simpleType);
			}

			public override void VisitMemberType (MemberType memberType)
			{
				base.VisitMemberType (memberType);
				if (memberType.MemberNameToken.StartLocation.Line != lineNumber) {
					return;
				}

				var result = resolver.Resolve (memberType, cancellationToken);

				if (result.IsError) {
					result = resolver.Resolve (memberType.Target, cancellationToken);
					if (result.IsError) {
						// base type is unresolved - it's already marked.
						return;
					}
					// if && csharpSyntaxMode.guiDocument.Project != null
					Colorize (memberType.MemberNameToken, "Syntax Error");
				}
				if (result is TypeResolveResult) {
					Colorize (memberType.MemberNameToken, GetUserTypeHighlighting (result.Type));
				}
			}


			public override void VisitMemberReferenceExpression (MemberReferenceExpression memberReferenceExpression)
			{
				base.VisitMemberReferenceExpression (memberReferenceExpression);
				if (memberReferenceExpression.MemberNameToken.StartLocation.Line != lineNumber)
					return;
				var result = resolver.Resolve (memberReferenceExpression, cancellationToken);
				if (result.IsError) {
					result = resolver.Resolve (memberReferenceExpression.Target, cancellationToken);
					if (result.IsError) {
						// target already is colorized
						return;
					}
					// if && csharpSyntaxMode.guiDocument.Project != null
					Colorize (memberReferenceExpression.MemberNameToken, "Syntax Error");
				}

				if (result is MemberResolveResult) {
					var member = ((MemberResolveResult)result).Member;
					switch (member.EntityType) {
					case EntityType.Field:
						if (!member.IsStatic && !((IField)member).IsConst)
							Colorize (memberReferenceExpression.MemberNameToken, "User Field Usage");
						break;
					case EntityType.Property:
						Colorize (memberReferenceExpression.MemberNameToken, "User Property Usage");
						break;
					case EntityType.Method:
						Colorize (memberReferenceExpression.MemberNameToken, "User Method Usage");
						break;
					case EntityType.Event:
						Colorize (memberReferenceExpression.MemberNameToken, "User Event Usage");
						break;
					}
				}

				if (result is MethodGroupResolveResult) {
					Colorize (memberReferenceExpression.MemberNameToken, "User Method Usage");
				}

				if (result is TypeResolveResult) {
					Colorize (memberReferenceExpression.MemberNameToken, GetUserTypeHighlighting (result.Type));
				}
			}

			public override void VisitTypeOfExpression (TypeOfExpression typeOfExpression)
			{
				var result = resolver.Resolve (typeOfExpression, cancellationToken) as TypeOfResolveResult;
				if (result != null && result.ReferencedType.Kind == TypeKind.Unknown) {
					Colorize (typeOfExpression.Type, "Syntax Error");
					return;
				}
				base.VisitTypeOfExpression (typeOfExpression);
			}
			
			

			public override void VisitPointerReferenceExpression (PointerReferenceExpression pointerReferenceExpression)
			{
				base.VisitPointerReferenceExpression (pointerReferenceExpression);
				var result = resolver.Resolve (pointerReferenceExpression, cancellationToken);
				if (result.IsError) {
					// if && csharpSyntaxMode.guiDocument.Project != null
					Colorize (pointerReferenceExpression.MemberNameToken, "Syntax Error");
				}

				if (result is MemberResolveResult) {
					var member = ((MemberResolveResult)result).Member;
					switch (member.EntityType) {
					case EntityType.Field:
						if (!member.IsStatic && !((IField)member).IsConst)
							Colorize (pointerReferenceExpression.MemberNameToken, "User Field Usage");
						break;
					case EntityType.Property:
						Colorize (pointerReferenceExpression.MemberNameToken, "User Property Usage");
						break;
					case EntityType.Method:
						Colorize (pointerReferenceExpression.MemberNameToken, "User Method Usage");
						break;
					}
				}

				if (result is MethodGroupResolveResult) {
					Colorize (pointerReferenceExpression.MemberNameToken, "User Method Usage");
				}

				if (result is TypeResolveResult) {
					Colorize (pointerReferenceExpression.MemberNameToken, GetUserTypeHighlighting (result.Type));
				}
			}
			public override void VisitQueryWhereClause (QueryWhereClause queryWhereClause)
			{
				base.VisitQueryWhereClause (queryWhereClause);
				if (queryWhereClause.WhereKeyword.StartLocation.Line == lineNumber)
					Colorize (queryWhereClause.WhereKeyword, contextualHighlightKeywords["where"]);

			}
			public override void VisitQueryFromClause (QueryFromClause queryFromClause)
			{
				base.VisitQueryFromClause (queryFromClause);
				if (queryFromClause.FromKeyword.StartLocation.Line == lineNumber)
					Colorize (queryFromClause.FromKeyword, contextualHighlightKeywords["from"]);
			}

			public override void VisitQuerySelectClause (QuerySelectClause querySelectClause)
			{
				base.VisitQuerySelectClause (querySelectClause);
				if (querySelectClause.SelectKeyword.StartLocation.Line == lineNumber)
					Colorize (querySelectClause.SelectKeyword, contextualHighlightKeywords["select"]);
			}

			public override void VisitQueryGroupClause (QueryGroupClause queryGroupClause)
			{
				base.VisitQueryGroupClause (queryGroupClause);
				if (queryGroupClause.GroupKeyword.StartLocation.Line == lineNumber)
					Colorize (queryGroupClause.GroupKeyword, contextualHighlightKeywords["group"]);
				if (queryGroupClause.ByKeyword.StartLocation.Line == lineNumber)
					Colorize (queryGroupClause.ByKeyword, contextualHighlightKeywords["by"]);
			}

			public override void VisitQueryContinuationClause (QueryContinuationClause queryContinuationClause)
			{
				base.VisitQueryContinuationClause (queryContinuationClause);
				if (queryContinuationClause.IntoKeyword.StartLocation.Line == lineNumber)
					Colorize (queryContinuationClause.IntoKeyword, contextualHighlightKeywords["into"]);
			}

			public override void VisitQueryJoinClause (QueryJoinClause queryJoinClause)
			{
				base.VisitQueryJoinClause (queryJoinClause);
				if (queryJoinClause.IntoKeyword.StartLocation.Line == lineNumber)
					Colorize (queryJoinClause.IntoKeyword, contextualHighlightKeywords["into"]);
				if (queryJoinClause.JoinKeyword.StartLocation.Line == lineNumber)
					Colorize (queryJoinClause.JoinKeyword, contextualHighlightKeywords["join"]);
				if (queryJoinClause.OnKeyword.StartLocation.Line == lineNumber)
					Colorize (queryJoinClause.OnKeyword, contextualHighlightKeywords["on"]);
				if (queryJoinClause.EqualsKeyword.StartLocation.Line == lineNumber)
					Colorize (queryJoinClause.EqualsKeyword, contextualHighlightKeywords["equals"]);
			}

			public override void VisitQueryLetClause (QueryLetClause queryLetClause)
			{
				base.VisitQueryLetClause (queryLetClause);
				if (queryLetClause.LetKeyword.StartLocation.Line == lineNumber)
					Colorize (queryLetClause.LetKeyword, contextualHighlightKeywords["let"]);
			}

			public override void VisitQueryOrdering (QueryOrdering queryOrdering)
			{
				base.VisitQueryOrdering (queryOrdering);
				if (queryOrdering.DirectionToken.StartLocation.Line == lineNumber)
					Colorize (queryOrdering.DirectionToken, contextualHighlightKeywords[queryOrdering.DirectionToken.GetText ()]);
			}
		}

		class QuickTaskVisitor : DepthFirstAstVisitor
		{
			internal List<QuickTask> QuickTasks = new List<QuickTask> ();
			readonly CSharpAstResolver resolver;
			readonly CancellationToken cancellationToken;

			public QuickTaskVisitor (CSharpAstResolver resolver, CancellationToken cancellationToken)
			{
				this.resolver = resolver;
			}
			
			protected override void VisitChildren (AstNode node)
			{
				if (cancellationToken.IsCancellationRequested)
					return;
				base.VisitChildren (node);
			}

			public override void VisitIdentifierExpression (IdentifierExpression identifierExpression)
			{
				base.VisitIdentifierExpression (identifierExpression);
				var result = resolver.Resolve (identifierExpression, cancellationToken);
				if (result.IsError) {
					QuickTasks.Add (new QuickTask (string.Format ("error CS0103: The name `{0}' does not exist in the current context", identifierExpression.Identifier), identifierExpression.StartLocation, Severity.Error));
				}
			}

			public override void VisitMemberReferenceExpression (MemberReferenceExpression memberReferenceExpression)
			{
				base.VisitMemberReferenceExpression (memberReferenceExpression);
				var result = resolver.Resolve (memberReferenceExpression, cancellationToken) as UnknownMemberResolveResult;
				if (result != null && result.TargetType.Kind != TypeKind.Unknown) {
					QuickTasks.Add (new QuickTask (string.Format ("error CS0117: `{0}' does not contain a definition for `{1}'", result.TargetType.FullName, memberReferenceExpression.MemberName), memberReferenceExpression.MemberNameToken.StartLocation, Severity.Error));
				}
			}
		}
		
		static CSharpSyntaxMode ()
		{
			MonoDevelop.Debugger.DebuggingService.DisableConditionalCompilation += DispatchService.GuiDispatch (new EventHandler<DocumentEventArgs> (OnDisableConditionalCompilation));
			IdeApp.Workspace.ActiveConfigurationChanged += delegate {
				foreach (var doc in IdeApp.Workbench.Documents) {
					TextEditorData data = doc.Editor;
					if (data == null)
						continue;
					// Force syntax mode reparse (required for #if directives)
					doc.Editor.Document.SyntaxMode = doc.Editor.Document.SyntaxMode;
					doc.ReparseDocument ();
				}
			};
			CommentTag.SpecialCommentTagsChanged += (sender, e) => {
				UpdateCommentRule ();
				var actDoc = IdeApp.Workbench.ActiveDocument;
				if (actDoc != null && actDoc.Editor != null) {
					actDoc.UpdateParseDocument ();
					actDoc.Editor.Parent.TextViewMargin.PurgeLayoutCache ();
					actDoc.Editor.Parent.QueueDraw ();
				}
			};
		}
		
		static void OnDisableConditionalCompilation (object s, DocumentEventArgs e)
		{
			var mode = e.Document.Editor.Document.SyntaxMode as CSharpSyntaxMode;
			if (mode == null)
				return;
			mode.DisableConditionalHighlighting = true;
			e.Document.Editor.Document.CommitUpdateAll ();
		}
		
		static Dictionary<string, string> contextualHighlightKeywords;
		static readonly string[] ContextualKeywords = new string[] {
			"value", //*
			"get", "set", "add", "remove",  //*
			"var", //*
			"global",
			"partial", //* 
			"where",  //*
			"select",
			"group",
			"by",
			"into",
			"from",
			"ascending",
			"descending",
			"orderby",
			"let",
			"join",
			"on",
			"equals"
		};

		#region Syntax mode rule cache
		static List<Rule> _rules;
		static List<Mono.TextEditor.Highlighting.Keywords> _keywords;
		static Span[] _spans;
		static Match[] _matches;
		static Marker[] _prevMarker;
		static List<SemanticRule> _SemanticRules;
		static Rule _commentRule;
		static Dictionary<string, Mono.TextEditor.Highlighting.Keywords> _keywordTable;
		static Dictionary<string, Mono.TextEditor.Highlighting.Keywords> _keywordTableIgnoreCase;
		static Dictionary<string, List<string>> _properties;
		#endregion

		static void UpdateCommentRule ()
		{
			if (_commentRule == null)
				return;
			var joinedTasks = string.Join ("", CommentTag.SpecialCommentTags.Select (t => t.Tag));
			_commentRule.Delimiter = new string ("&()<>{}[]~!%^*-+=|\\#/:;\"' ,\t.?".Where (c => joinedTasks.IndexOf (c) < 0).ToArray ());
			_commentRule.Keywords = new[] {
				new Keywords {
					Color = "Comment Tag",
					Words = CommentTag.SpecialCommentTags.Select (t => t.Tag)
				}
			};
		}

		public CSharpSyntaxMode (Document document)
		{
			this.guiDocument = document;
			guiDocument.DocumentParsed += HandleDocumentParsed;
			SemanticHighlightingEnabled = PropertyService.Get ("EnableSemanticHighlighting", true);
			PropertyService.PropertyChanged += HandlePropertyChanged;
			if (guiDocument.ParsedDocument != null)
				HandleDocumentParsed (this, EventArgs.Empty);

			bool loadRules = _rules == null;

			if (loadRules) {
				var provider = new ResourceStreamProvider (typeof(ResourceStreamProvider).Assembly, typeof(ResourceStreamProvider).Assembly.GetManifestResourceNames ().First (s => s.Contains ("CSharpSyntaxMode")));
				using (var reader = provider.Open ()) {
					SyntaxMode baseMode = SyntaxMode.Read (reader);
					_rules = new List<Rule> (baseMode.Rules.Where (r => r.Name != "Comment"));
					_rules.Add (new Rule (this) {
						Name = "PreProcessorComment"
					});

					_commentRule = new Rule (this) {
						Name = "Comment",
						IgnoreCase = true
					};
					UpdateCommentRule ();

					_rules.Add (_commentRule);
					_keywords = new List<Keywords> (baseMode.Keywords);
					_spans = new List<Span> (baseMode.Spans.Where (span => span.Begin.Pattern != "#")).ToArray ();
					_matches = baseMode.Matches;
					_prevMarker = baseMode.PrevMarker;
					_SemanticRules = new List<SemanticRule> (baseMode.SemanticRules);
					_keywordTable = baseMode.keywordTable;
					_keywordTableIgnoreCase = baseMode.keywordTableIgnoreCase;
					_properties = baseMode.Properties;
				}

				contextualHighlightKeywords = new Dictionary<string, string> ();
				foreach (var word in ContextualKeywords) {
					if (_keywordTable.ContainsKey (word)) {
						contextualHighlightKeywords[word] = _keywordTable[word].Color;
					} else {
						Console.WriteLine ("missing keyword:"+word);
					}
				}

				foreach (var word in ContextualKeywords) {
					_keywordTable.Remove (word);
				}
			}

			rules = _rules;
			keywords = _keywords;
			spans = _spans;
			matches = _matches;
			prevMarker = _prevMarker;
			SemanticRules = _SemanticRules;
			keywordTable = _keywordTable;
			keywordTableIgnoreCase = _keywordTableIgnoreCase;
			properties = _properties;

			if (loadRules) {
				AddSemanticRule ("Comment", new HighlightUrlSemanticRule ("Comment(Line)"));
				AddSemanticRule ("XmlDocumentation", new HighlightUrlSemanticRule ("Comment(Doc)"));
				AddSemanticRule ("String", new HighlightUrlSemanticRule ("String"));
			}
		}

		#region IDisposable implementation

		public void Dispose ()
		{
			if (src != null)
				src.Cancel ();
			guiDocument.DocumentParsed -= HandleDocumentParsed;
			PropertyService.PropertyChanged -= HandlePropertyChanged;
		}

		#endregion

		void HandlePropertyChanged (object sender, PropertyChangedEventArgs e)
		{
			if (e.Key == "EnableSemanticHighlighting")
				SemanticHighlightingEnabled = PropertyService.Get ("EnableSemanticHighlighting", true);
		}

		public override SpanParser CreateSpanParser (DocumentLine line, CloneableStack<Span> spanStack)
		{
			return new CSharpSpanParser (this, spanStack ?? line.StartSpan.Clone ());
		}
		
		public override ChunkParser CreateChunkParser (SpanParser spanParser, ColorScheme style, DocumentLine line)
		{
			return new CSharpChunkParser (this, spanParser, style, line);
		}
		
		abstract class AbstractBlockSpan : Span
		{
			public bool IsValid {
				get;
				private set;
			}
			
			bool disabled;
			
			public bool Disabled {
				get { return disabled; }
				set { disabled = value; SetColor (); }
			}
			
			
			public AbstractBlockSpan (bool isValid)
			{
				IsValid = isValid;
				SetColor ();
				StopAtEol = false;
			}
			
			protected void SetColor ()
			{
				TagColor = "Preprocessor";
				if (disabled || !IsValid) {
					Color = "Excluded Code";
					Rule = "PreProcessorComment";
				} else {
					Color = "Plain Text";
					Rule = "<root>";
				}
			}
		}

		class DefineSpan : Span
		{
			string define;

			public string Define { 
				get { 
					return define;
				}
			}

			public DefineSpan (string define)
			{
				this.define = define;
				StopAtEol = false;
				Color = "Plain Text";
				Rule = "<root>";
			}
		}

		class IfBlockSpan : AbstractBlockSpan
		{
			public IfBlockSpan (bool isValid) : base (isValid)
			{
			}
			
			public override string ToString ()
			{
				return string.Format("[IfBlockSpan: IsValid={0}, Disabled={3}, Color={1}, Rule={2}]", IsValid, Color, Rule, Disabled);
			}
		}
		
		class ElseIfBlockSpan : AbstractBlockSpan
		{
			public ElseIfBlockSpan (bool isValid) : base (isValid)
			{
				base.Begin = new Regex ("#elif");
			}
			
			public override string ToString ()
			{
				return string.Format("[ElseIfBlockSpan: IsValid={0}, Disabled={3}, Color={1}, Rule={2}]", IsValid, Color, Rule, Disabled);
			}
		}
		
		class ElseBlockSpan : AbstractBlockSpan
		{
			public ElseBlockSpan (bool isValid) : base (isValid)
			{
				base.Begin = new Regex ("#else");
			}
			
			public override string ToString ()
			{
				return string.Format("[ElseBlockSpan: IsValid={0}, Disabled={3}, Color={1}, Rule={2}]", IsValid, Color, Rule, Disabled);
			}
		}
		
		protected class CSharpChunkParser : ChunkParser, IResolveVisitorNavigator
		{

			HashSet<string> tags = new HashSet<string> ();
			
			CSharpSyntaxMode csharpSyntaxMode;
			int lineNumber;
			public CSharpChunkParser (CSharpSyntaxMode csharpSyntaxMode, SpanParser spanParser, ColorScheme style, DocumentLine line) : base (csharpSyntaxMode, spanParser, style, line)
			{
				lineNumber = line.LineNumber;
				this.csharpSyntaxMode = csharpSyntaxMode;
				foreach (var tag in CommentTag.SpecialCommentTags) {
					tags.Add (tag.Tag);
				}

			}

			#region IResolveVisitorNavigator implementation
			ResolveVisitorNavigationMode IResolveVisitorNavigator.Scan(AstNode node)
			{
				if (node is SimpleType || node is MemberType
					|| node is IdentifierExpression || node is MemberReferenceExpression
					|| node is InvocationExpression) {
					return ResolveVisitorNavigationMode.Resolve;
				}
				return ResolveVisitorNavigationMode.Scan;
			}
			
			void IResolveVisitorNavigator.Resolved(AstNode node, ResolveResult result)
			{
			}
			
			void IResolveVisitorNavigator.ProcessConversion(Expression expression, ResolveResult result, Conversion conversion, IType targetType)
			{
			}
			#endregion
			static int TokenLength (AstNode node)
			{
				Debug.Assert (node.StartLocation.Line == node.EndLocation.Line);
				return node.EndLocation.Column - node.StartLocation.Column;
			}

			protected override void AddRealChunk (Chunk chunk)
			{
				var document = csharpSyntaxMode.guiDocument;
				var parsedDocument = document != null ? document.ParsedDocument : null;
				if (parsedDocument != null && csharpSyntaxMode.SemanticHighlightingEnabled && csharpSyntaxMode.resolver != null) {
					int endLoc = -1;
					string semanticStyle = null;
					if (spanParser.CurSpan == null || spanParser.CurSpan is DefineSpan || spanParser.CurSpan is AbstractBlockSpan) {
						try {
							HighlightingVisitior visitor;
							if (!csharpSyntaxMode.lineSegments.TryGetValue (line, out visitor)) {
								visitor = new HighlightingVisitior (csharpSyntaxMode.resolver, default (CancellationToken), lineNumber, base.line.Offset);
								visitor.tree.InstallListener (doc);
								csharpSyntaxMode.unit.AcceptVisitor (visitor);
								csharpSyntaxMode.lineSegments[line] = visitor;
							}
							string style;
							if (visitor.tree.GetStyle (chunk, ref endLoc, out style)) {
								semanticStyle = style;
							}
						} catch (Exception e) {
							Console.WriteLine ("Error in semantic highlighting: " + e);
						}
					}
					if (semanticStyle != null) {
						if (endLoc < chunk.EndOffset) {
							base.AddRealChunk (new Chunk (chunk.Offset, endLoc - chunk.Offset, semanticStyle));
							base.AddRealChunk (new Chunk (endLoc, chunk.EndOffset - endLoc, chunk.Style));
							return;
						}
						chunk.Style = semanticStyle;
					}
				}
				
				base.AddRealChunk (chunk);
			}
			
			protected override string GetStyle (Chunk chunk)
			{
				if (spanParser.CurRule.Name == "Comment") {
					if (tags.Contains (doc.GetTextAt (chunk))) 
						return "Comment Tag";
				}
				return base.GetStyle (chunk);
			}
		}
		
		protected class CSharpSpanParser : SpanParser
		{
			CSharpSyntaxMode CSharpSyntaxMode {
				get {
					return (CSharpSyntaxMode)mode;
				}
			}
			class ConditinalExpressionEvaluator : DepthFirstAstVisitor<object, object>
			{
				HashSet<string> symbols;

				MonoDevelop.Projects.Project GetProject (TextDocument doc)
				{
					// There is no reference between document & higher level infrastructure,
					// therefore it's a bit tricky to find the right project.
					
					MonoDevelop.Projects.Project project = null;
					var view = doc.Annotation<MonoDevelop.SourceEditor.SourceEditorView> ();
					if (view != null)
						project = view.Project;
					
					if (project == null) {
						var ideDocument = IdeApp.Workbench.GetDocument (doc.FileName);
						if (ideDocument != null)
							project = ideDocument.Project;
					}
					
					if (project == null)
						project = IdeApp.Workspace.GetProjectContainingFile (doc.FileName);
					
					return project;
				}

				public ConditinalExpressionEvaluator (TextDocument doc, IEnumerable<string> symbols)
				{
					this.symbols = new HashSet<string> (symbols);
					var project = GetProject (doc);
					
					if (project == null) {
						var ideDocument = IdeApp.Workbench.GetDocument (doc.FileName);
						if (ideDocument != null)
							project = ideDocument.Project;
					}
					
					if (project == null)
						project = IdeApp.Workspace.GetProjectContainingFile (doc.FileName);
					
					if (project != null) {
						var configuration = project.GetConfiguration (IdeApp.Workspace.ActiveConfiguration) as DotNetProjectConfiguration;
						if (configuration != null) {
							var cparams = configuration.CompilationParameters as CSharpCompilerParameters;
							if (cparams != null) {
								string[] syms = cparams.DefineSymbols.Split (';', ',', ' ', '\t');
								foreach (string s in syms) {
									string ss = s.Trim ();
									if (ss.Length > 0 && !symbols.Contains (ss))
										this.symbols.Add (ss);
								}
							}
							// Workaround for mcs defined symbol
							if (configuration.TargetRuntime.RuntimeId == "Mono") 
								this.symbols.Add ("__MonoCS__");
						} else {
							Console.WriteLine ("NO CONFIGURATION");
						}
					}
/*					var parsedDocument = TypeSystemService.ParseFile (document.ProjectContent, doc.FileName, doc.MimeType, doc.Text);
					if (parsedDocument == null)
						parsedDocument = TypeSystemService.ParseFile (dom, doc.FileName ?? "a.cs", delegate { return doc.Text; });
					if (parsedDocument != null) {
						foreach (PreProcessorDefine define in parsedDocument.Defines) {
							symbols.Add (define.Define);
						}
						
					}*/
				}
				
				public override object VisitIdentifierExpression (IdentifierExpression identifierExpression, object data)
				{
					return symbols.Contains (identifierExpression.Identifier);
				}
				
				public override object VisitUnaryOperatorExpression (UnaryOperatorExpression unaryOperatorExpression, object data)
				{
					bool result = (bool)(unaryOperatorExpression.Expression.AcceptVisitor (this, data) ?? false);
					if (unaryOperatorExpression.Operator ==  UnaryOperatorType.Not)
						return !result;
					return result;
				}


				public override object VisitPrimitiveExpression (PrimitiveExpression primitiveExpression, object data)
				{
					if (primitiveExpression.Value is bool)
						return primitiveExpression.Value;
					return false;
				}

				public override object VisitBinaryOperatorExpression (BinaryOperatorExpression binaryOperatorExpression, object data)
				{
					bool left = (bool)(binaryOperatorExpression.Left.AcceptVisitor (this, data) ?? false);
					bool right = (bool)(binaryOperatorExpression.Right.AcceptVisitor (this, data) ?? false);
					switch (binaryOperatorExpression.Operator) {
					case BinaryOperatorType.InEquality:
						return left != right;
					case BinaryOperatorType.Equality:
						return left == right;
					case BinaryOperatorType.ConditionalOr:
						return left || right;
					case BinaryOperatorType.ConditionalAnd:
						return left && right;
					}
					
					Console.WriteLine ("Unknown operator:" + binaryOperatorExpression.Operator);
					return left;
				}

				public override object VisitParenthesizedExpression (ParenthesizedExpression parenthesizedExpression, object data)
				{
					return parenthesizedExpression.Expression.AcceptVisitor (this, data);
				}

			}
			
			void ScanPreProcessorElse (ref int i)
			{
				if (!spanStack.Any (s => s is IfBlockSpan || s is ElseIfBlockSpan)) {
					base.ScanSpan (ref i);
					return;
				}
				bool previousResult = false;
				foreach (Span span in spanStack) {
					if (span is IfBlockSpan) {
						previousResult = ((IfBlockSpan)span).IsValid;
					}
					if (span is ElseIfBlockSpan) {
						previousResult |= ((ElseIfBlockSpan)span).IsValid;
					}
				}
				//					LineSegment line = doc.GetLineByOffset (i);
				//					int length = line.Offset + line.EditableLength - i;
				while (spanStack.Count > 0 && !(CurSpan is IfBlockSpan || CurSpan is ElseIfBlockSpan)) {
					spanStack.Pop ();
				}
				var ifBlock = CurSpan as IfBlockSpan;
				var elseIfBlock = CurSpan as ElseIfBlockSpan;
				var elseBlockSpan = new ElseBlockSpan (!previousResult);
				if (ifBlock != null) {
					elseBlockSpan.Disabled = ifBlock.Disabled;
				} else if (elseIfBlock != null) {
					elseBlockSpan.Disabled = elseIfBlock.Disabled;
				}
				FoundSpanBegin (elseBlockSpan, i, "#else".Length);
				i += "#else".Length;
					
				// put pre processor eol span on stack, so that '#elif' gets the correct highlight
				Span preprocessorSpan = CreatePreprocessorSpan ();
				FoundSpanBegin (preprocessorSpan, i, 0);
			}
			IEnumerable<string> Defines {
				get {
					if (SpanStack == null)
						yield break;
					foreach (var span in SpanStack) {
						if (span is DefineSpan) {
							var define = ((DefineSpan)span).Define;
							if (define != null)
								yield return define;
						}
					}
				}
			}
			void ScanPreProcessorIf (int textOffset, ref int i)
			{
				var end = CurText.Length;
				int idx = 0;
				while ((idx = CurText.IndexOf ('/', idx)) >= 0 && idx + 1 < CurText.Length) {
					var next = CurText [idx + 1];
					if (next == '/') {
						end = idx - 1;
						break;
					}
					idx++;
				}

				int length = end - textOffset;
				string parameter = CurText.Substring (textOffset + 3, length - 3);
				AstNode expr = new CSharpParser ().ParseExpression (parameter);
				bool result = false;
				if (expr != null && !expr.IsNull) {
					object o = expr.AcceptVisitor (new ConditinalExpressionEvaluator (doc, Defines), null);
					if (o is bool)
						result = (bool)o;
				}
					
				foreach (Span span in spanStack) {
					if (span is IfBlockSpan) {
						result &= ((IfBlockSpan)span).IsValid;
					}
					if (span is ElseIfBlockSpan) {
						result &= ((ElseIfBlockSpan)span).IsValid;
					}
				}
					
				var ifBlockSpan = new IfBlockSpan (result);
					
				foreach (Span span in spanStack) {
					if (span is AbstractBlockSpan) {
						var parentBlock = (AbstractBlockSpan)span;
						ifBlockSpan.Disabled = parentBlock.Disabled || !parentBlock.IsValid;
						break;
					}
				}
					
				FoundSpanBegin (ifBlockSpan, i, length);
				i += length - 1;
			}

			void ScanPreProcessorElseIf (ref int i)
			{
				DocumentLine line = doc.GetLineByOffset (i);
				int length = line.Offset + line.Length - i;
				string parameter = doc.GetTextAt (i + 5, length - 5);
				AstNode expr= new CSharpParser ().ParseExpression (parameter);
				bool result;
				if (expr != null && !expr.IsNull) {
					var visitResult = expr.AcceptVisitor (new ConditinalExpressionEvaluator (doc, Defines), null);
					result = visitResult != null ? (bool)visitResult : false;
				} else {
					result = false;
				}
					
				IfBlockSpan containingIf = null;
				if (result) {
					bool previousResult = false;
					foreach (Span span in spanStack) {
						if (span is IfBlockSpan) {
							containingIf = (IfBlockSpan)span;
							previousResult = ((IfBlockSpan)span).IsValid;
							break;
						}
						if (span is ElseIfBlockSpan) {
							previousResult |= ((ElseIfBlockSpan)span).IsValid;
						}
					}
						
					result = !previousResult;
				}
					
				var elseIfBlockSpan = new ElseIfBlockSpan (result);
				if (containingIf != null)
					elseIfBlockSpan.Disabled = containingIf.Disabled;
					
				FoundSpanBegin (elseIfBlockSpan, i, 0);
					
				// put pre processor eol span on stack, so that '#elif' gets the correct highlight
				var preprocessorSpan = CreatePreprocessorSpan ();
				FoundSpanBegin (preprocessorSpan, i, 0);
			}

			protected override bool ScanSpan (ref int i)
			{
				if (CSharpSyntaxMode.DisableConditionalHighlighting) {
					return base.ScanSpan (ref i);
				}
				int textOffset = i - StartOffset;

				if (textOffset < CurText.Length && CurRule.Name != "Comment" && CurRule.Name != "String" && CurText [textOffset] == '#' && IsFirstNonWsChar (textOffset)) {

					if (CurText.IsAt (textOffset, "#define") && (spanStack == null || !spanStack.Any (span => span is IfBlockSpan && !((IfBlockSpan)span).IsValid))) {
						int length = CurText.Length - textOffset;
						string parameter = CurText.Substring (textOffset + "#define".Length, length - "#define".Length).Trim ();
						var defineSpan = new DefineSpan (parameter);
						FoundSpanBegin (defineSpan, i, 0);
					}
	
					if (CurText.IsAt (textOffset, "#else")) {
						ScanPreProcessorElse (ref i);
						return true;
					}
	
					if (CurText.IsAt (textOffset, "#if")) {
						ScanPreProcessorIf (textOffset, ref i);
						return true;
					}
	
					if (CurText.IsAt (textOffset, "#elif") && spanStack != null && spanStack.Any (span => span is IfBlockSpan)) {
						ScanPreProcessorElseIf (ref i);
						return true;
					}
	
					var preprocessorSpan = CreatePreprocessorSpan ();
					FoundSpanBegin (preprocessorSpan, i, 1);
					return true;
				}

				return base.ScanSpan (ref i);
			}
			
			public static Span CreatePreprocessorSpan ()
			{
				var result = new Span ();
				result.TagColor = "Preprocessor";
				result.Color = "Preprocessor";
				result.Rule = "String";
				result.StopAtEol = true;
				return result;
			}
			
			void PopCurrentIfBlock ()
			{
				while (spanStack.Count > 0 && (spanStack.Peek () is IfBlockSpan || spanStack.Peek () is ElseIfBlockSpan || spanStack.Peek () is ElseBlockSpan)) {
					var poppedSpan = PopSpan ();
					if (poppedSpan is IfBlockSpan)
						break;
				}
			}
			
			protected override bool ScanSpanEnd (Span cur, ref int i)
			{
				if (cur is IfBlockSpan || cur is ElseIfBlockSpan || cur is ElseBlockSpan) {
					int textOffset = i - StartOffset;
					bool end = CurText.IsAt (textOffset, "#endif");
					if (end) {
						FoundSpanEnd (cur, i, 6); // put empty end tag in
						
						// if we're in a complex span stack pop it up to the if block
						if (spanStack.Count > 0) {
							var prev = spanStack.Peek ();
							
							if ((cur is ElseIfBlockSpan || cur is ElseBlockSpan) && (prev is ElseIfBlockSpan || prev is IfBlockSpan))
								PopCurrentIfBlock ();
						}
					}
					return end;
				}
				return base.ScanSpanEnd (cur, ref i);
			}
			
	//		Span preprocessorSpan;
	//		Rule preprocessorRule;
			
			public CSharpSpanParser (CSharpSyntaxMode mode, CloneableStack<Span> spanStack) : base (mode, spanStack)
			{
//				foreach (Span span in mode.Spans) {
//					if (span.Rule == "text.preprocessor") {
//						preprocessorSpan = span;
//						preprocessorRule = GetRule (span);
//					}
//				}
			}
		}

		#region IQuickTaskProvider implementation
		public event EventHandler TasksUpdated;

		protected virtual void OnTasksUpdated (EventArgs e)
		{
			var handler = TasksUpdated;
			if (handler != null)
				handler (this, e);
		}

		List<QuickTask> quickTasks;
		public IEnumerable<QuickTask> QuickTasks {
			get {
				return quickTasks;
			}
		}
		#endregion
	}
}
 
