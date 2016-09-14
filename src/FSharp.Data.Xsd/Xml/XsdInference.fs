﻿// --------------------------------------------------------------------------------------
// Implements XML type inference from XSD
// --------------------------------------------------------------------------------------

// The XML Provider infers a type from sample documents: an instance of InferedType 
// represents elements having a structure compatible with the given samples.
// When a schema is available we can use it to derive an InferedType representing
// valid elements according to the definitions in the given schema.
// The InferedType derived from a schema should be essentialy the same as one
// infered from a significant set of valid samples.
// Adopting this perspective we can support XSD leveraging the existing functionalities.
// The implementation uses a simplfied XSD model to split the task of deriving an InferedType:
// - element definitions in xsd files map to this simplified xsd model
// - instances of this xsd model map to InferedType.


namespace ProviderImplementation

open System.Xml
open System.Xml.Schema

/// Simplified model to represent schemas (XSD).
module XsdModel =
    
    type IsOptional = bool
    type Occurs = decimal * decimal

    // reference equality and mutable type allows for cycles
    [<ReferenceEquality>] 
    type XsdElement = { Name: XmlQualifiedName
                        mutable Type: XsdType
                        SubstitutionGroup: XsdElement list
                        IsAbstract: bool
                        IsNillable: bool }

    and XsdType = SimpleType of XmlTypeCode | ComplexType of XsdComplexType

    and [<ReferenceEquality>] XsdComplexType = 
        { Attributes: (XmlQualifiedName * XmlTypeCode * IsOptional) list
          Contents: XsdContent }

    and XsdContent = SimpleContent of XmlTypeCode | ComplexContent of XsdParticle

    and XsdParticle = 
        | Empty
        | Any      of Occurs 
        | Element  of Occurs * XsdElement
        | All      of Occurs * XsdParticle list
        | Choice   of Occurs * XsdParticle list
        | Sequence of Occurs * XsdParticle list

/// A simplified schema model is built from xsd. 
/// The actual parsing is done using BCL classes.
module XsdParsing =

    let ofType<'a> (sequence: System.Collections.IEnumerable) =
        sequence
        |> Seq.cast<obj>
        |> Seq.filter (fun x -> x :? 'a)
        |> Seq.cast<'a>
        

    type ParsingContext(xmlSchemaSet: XmlSchemaSet) =
        
        let getElm name = // lookup elements by name
            xmlSchemaSet.GlobalElements.Item name :?> XmlSchemaElement

        let subst = // lookup of substitution group members 
            xmlSchemaSet.GlobalElements.Values
            |> ofType<XmlSchemaElement>
            |> Seq.filter (fun e -> not e.SubstitutionGroup.IsEmpty)
            |> Seq.groupBy (fun e -> e.SubstitutionGroup)
            |> Seq.map (fun (name, values) -> getElm name, values |> List.ofSeq)
            |> dict

        let getSubst =     
            // deep lookup for trees of substitution groups, see 
            // http://docstore.mik.ua/orelly/xml/schema/ch12_01.htm#xmlschema-CHP-12-SECT-1       
            let collectSubst elm = 
                let items = System.Collections.Generic.HashSet()
                let rec collect elm =
                    if subst.ContainsKey elm then
                        for x in subst.Item elm do 
                            if items.Add x then collect x 
                collect elm 
                items |> List.ofSeq

            let subst' =
                subst.Keys
                |> Seq.map (fun x -> x, collectSubst x)
                |> dict

            fun elm -> if subst'.ContainsKey elm then subst'.Item elm else []
        

        let elements = System.Collections.Generic.Dictionary<XmlSchemaElement, XsdModel.XsdElement>()

        member x.getElement name = getElm name
        member x.getSubstitutions elm = getSubst elm
        member x.Elements = elements


    open XsdModel

    let rec parseElement (ctx: ParsingContext) elm = 
        match ctx.Elements.TryGetValue elm with
        | true, x -> x
        | _ ->
        let substitutionGroup = 
            ctx.getSubstitutions elm 
            |> List.filter (fun x -> x <> elm) 
            |> List.map (parseElement ctx)
        // another attempt in case the element is put while parsing substitution groups
        match ctx.Elements.TryGetValue elm with
        | true, x -> x
        | _ ->
        let result =
            { Name = elm.QualifiedName
              Type = XsdType.SimpleType XmlTypeCode.None // temporary dummy value
              SubstitutionGroup = substitutionGroup
              IsAbstract = elm.IsAbstract
              IsNillable = elm.IsNillable }
        ctx.Elements.Add(elm, result)
        // computing the real type after filling the dictionary allows for cycles
        result.Type <- 
            match elm.ElementSchemaType with
            | :? XmlSchemaSimpleType  as x -> SimpleType x.Datatype.TypeCode
            | :? XmlSchemaComplexType as x -> ComplexType (parseComplexType ctx x)
            | x -> failwithf "unknown ElementSchemaType: %A" x
        result 

    and parseComplexType (ctx: ParsingContext) (x: XmlSchemaComplexType) =  
        { Attributes = 
            x.AttributeUses.Values 
            |> ofType<XmlSchemaAttribute>
            |> Seq.filter (fun a -> a.Use <> XmlSchemaUse.Prohibited)
            |> Seq.map (fun a -> 
                a.QualifiedName,
                a.AttributeSchemaType.Datatype.TypeCode, 
                a.Use <> XmlSchemaUse.Required)
            |> List.ofSeq
          Contents = 
            match x.ContentType with
            | XmlSchemaContentType.TextOnly -> SimpleContent x.Datatype.TypeCode
            | XmlSchemaContentType.Mixed 
            | XmlSchemaContentType.Empty 
            | XmlSchemaContentType.ElementOnly -> 
                x.ContentTypeParticle |> parseParticle ctx |> ComplexContent
            | _ -> failwithf "Unknown content type: %A." x.ContentType }


    and parseParticle (ctx: ParsingContext) (par: XmlSchemaParticle) =  

        let occurs = par.MinOccurs, par.MaxOccurs

        let parseParticles (group: XmlSchemaGroupBase) =
            let particles = 
                group.Items
                |> ofType<XmlSchemaParticle> 
                |> Seq.map (parseParticle ctx)
                |> List.ofSeq
            match group with
            | :? XmlSchemaAll      -> All (occurs, particles)
            | :? XmlSchemaChoice   -> Choice (occurs, particles)
            | :? XmlSchemaSequence -> Sequence (occurs, particles)
            | _ -> failwithf "unknown group base: %A" group

        let result =
            match par with
            | :? XmlSchemaAny -> Any occurs
            | :? XmlSchemaGroupBase as grp -> parseParticles grp
            | :? XmlSchemaGroupRef as grpRef -> parseParticle ctx grpRef.Particle
            | :? XmlSchemaElement as elm -> 
                let e = if elm.RefName.IsEmpty then elm else ctx.getElement elm.RefName
                Element (occurs, parseElement ctx e)
            | _ -> Empty // XmlSchemaParticle.EmptyParticle
        result

    let getElements schema =
        let ctx = ParsingContext schema
        schema.GlobalElements.Values 
        |> ofType<XmlSchemaElement>
        |> Seq.filter (fun x -> x.ElementSchemaType :? XmlSchemaComplexType )
        |> Seq.map (parseElement ctx)


    
    open FSharp.Data.Runtime

    /// A custom XmlResolver is needed for included files because we get the contents of the main file 
    /// directly as a string from the FSharp.Data infrastructure. Hence the default XmlResolver is not
    /// able to find the location of included schema files.
    type ResolutionFolderResolver(resolutionFolder: string) =
        inherit XmlUrlResolver()

        let cache, _ = Caching.createInternetFileCache "XmlSchema" (System.TimeSpan.FromMinutes 30.0)

        let uri = // TODO improve this
            if resolutionFolder = "" then ""
            elif resolutionFolder.EndsWith "/" || resolutionFolder.EndsWith "\\"
            then resolutionFolder
            else resolutionFolder + "/"

        let useResolutionFolder (baseUri: System.Uri) = 
            resolutionFolder <> "" && (baseUri = null || baseUri.OriginalString = "")


        override _this.ResolveUri(baseUri, relativeUri) = 
            let u = System.Uri(relativeUri, System.UriKind.RelativeOrAbsolute)
            let result =
                if u.IsAbsoluteUri && (not <| u.IsFile)
                then base.ResolveUri(baseUri, relativeUri)
                elif useResolutionFolder baseUri
                then base.ResolveUri(System.Uri uri, relativeUri)
                else base.ResolveUri(baseUri, relativeUri)
            result

        override _this.GetEntity(absoluteUri, role, ofObjectToReturn) =
            if IO.isWeb absoluteUri 
            then
                let uri = absoluteUri.OriginalString
                match cache.TryRetrieve uri with
                | Some value -> value
                | None ->
                    let value = FSharp.Data.Http.RequestString uri //TODO async?
                    cache.Set(uri, value)
                    value
                |> fun value ->
                    // what if it's not UTF8? probably we should peek the xml string
                    // looking for an encoding declaration.
                    // the best solution would be to have the cache store the raw bytes
                    let bytes = System.Text.Encoding.UTF8.GetBytes value
                    new System.IO.MemoryStream(bytes) :> obj
            else base.GetEntity(absoluteUri, role, ofObjectToReturn)

    
    let parseSchema resolutionFolder xsdText =
        let schemaSet = XmlSchemaSet() 
        schemaSet.XmlResolver <- ResolutionFolderResolver resolutionFolder
        let readerSettings = XmlReaderSettings()
        readerSettings.CloseInput <- true
        readerSettings.DtdProcessing <- DtdProcessing.Ignore
        use reader = XmlReader.Create(new System.IO.StringReader(xsdText), readerSettings)
        schemaSet.Add(null, reader) |> ignore
        schemaSet.Compile()
        schemaSet



/// Element definitions in a schema are mapped to InferedType instances
module XsdInference =
    open XsdModel
    open FSharp.Data.Runtime.StructuralTypes

    // for now we map only the types supported in F# Data
    let getType = function
        | XmlTypeCode.Int -> typeof<int>
        | XmlTypeCode.Long -> typeof<int64>
        | XmlTypeCode.Date -> typeof<System.DateTime>
        | XmlTypeCode.DateTime -> typeof<System.DateTime>
        | XmlTypeCode.Boolean -> typeof<bool>
        | XmlTypeCode.Decimal -> typeof<decimal>
        | XmlTypeCode.Double -> typeof<double>
        // fallback to string
        | _ -> typeof<string>

    let getMultiplicity = function
        | 1M, 1M -> Single
        | 0M, 1M -> OptionalSingle
        | _ -> Multiple

    // how multiplicity is affected when nesting particles
    let combineMultiplicity = function
        | Single, x -> x
        | Multiple, _ -> Multiple
        | _, Multiple -> Multiple
        | OptionalSingle, _ -> OptionalSingle
       
    // the effect of a choice is to make mandatory items optional 
    let makeOptional = function Single -> OptionalSingle | x -> x

    let getElementName (elm: XsdElement) =
        if elm.Name.Namespace = "" 
        then Some elm.Name.Name
        else Some (sprintf "{%s}%s" elm.Name.Namespace elm.Name.Name)

    let nil = { InferedProperty.Name = "{http://www.w3.org/2001/XMLSchema-instance}nil"
                Type = InferedType.Primitive(typeof<bool>, None, true) }

    type Ctx = System.Collections.Generic.Dictionary<XsdComplexType, InferedProperty>

    // derives an InferedType for an element definition
    let rec inferElementType (ctx: Ctx) (elm: XsdElement) =
        let name = getElementName elm
        if elm.IsAbstract 
        then InferedType.Record(name, [], optional = false)
        else
            match elm.Type with
            | SimpleType typeCode ->
                let ty = InferedType.Primitive (getType typeCode, None, elm.IsNillable)
                let prop = { InferedProperty.Name = ""; Type = ty }
                let props = if elm.IsNillable then [prop; nil] else [prop]
                InferedType.Record(name, props, optional = false)
            | ComplexType cty -> 
                let props = inferProperties ctx cty
                let props = 
                    if elm.IsNillable then 
                        for prop in props do 
                            prop.Type <- prop.Type.EnsuresHandlesMissingValues false
                        nil::props 
                    else props
                InferedType.Record(name, props, optional = false)
            

    and inferProperties (ctx: Ctx) cty =
        let attrs: InferedProperty list = 
            cty.Attributes
            |> List.map (fun (name, typeCode, optional) ->
                { Name = name.Name
                  Type = InferedType.Primitive (getType typeCode, None, optional) } )
         
        match cty.Contents with
        | SimpleContent typeCode -> 
            let body = { InferedProperty.Name = ""
                         Type = InferedType.Primitive (getType typeCode, None, false)}
            body::attrs
        | ComplexContent xsdParticle ->
            let body = 
                if ctx.ContainsKey cty then ctx.Item cty else
                let result = { InferedProperty.Name = ""; Type = InferedType.Top }
                ctx.Add(cty, result)
                let getRecordTag (e:XsdElement) = InferedTypeTag.Record(getElementName e)
                result.Type <-
                    match getElements ctx Single xsdParticle with
                    | [] -> InferedType.Null
                    | items ->
                        let tags = items |> List.map (fst >> getRecordTag)
                        let types = 
                            items 
                            |> Seq.zip tags
                            |> Seq.map (fun (tag, (e, m)) -> tag, (m, inferElementType ctx e))
                            |> Map.ofSeq
                        InferedType.Collection(tags, types)
                result
            if body.Type = InferedType.Null then attrs else body::attrs
       
    // collects element definitions in a particle
    and getElements (ctx: Ctx) parentMultiplicity = function
        | XsdParticle.Element(occ, elm) -> 
            let mult = combineMultiplicity(parentMultiplicity, getMultiplicity occ)
            match elm.IsAbstract, elm.SubstitutionGroup with
            | _, [] -> [ (elm, mult) ]
            | true, [x] -> [ (x, mult) ]
            | true, x -> x |> List.map (fun e -> e,  makeOptional mult)
            | false, x -> elm::x |> List.map (fun e -> e,  makeOptional mult)
        | XsdParticle.Sequence (occ, particles)
        | XsdParticle.All (occ, particles) -> 
            let mult = combineMultiplicity(parentMultiplicity, getMultiplicity occ)
            particles |> List.collect (getElements ctx mult)
        | XsdParticle.Choice (occ, particles) -> 
            let mult = makeOptional (getMultiplicity occ)
            let mult' = combineMultiplicity(parentMultiplicity, mult)
            particles |> List.collect (getElements ctx mult')
        | XsdParticle.Empty -> []
        | XsdParticle.Any _ -> []


    let inferElements elms = 
        match elms |> List.filter (fun elm -> not elm.IsAbstract) with
        | [] -> failwith "No suitable element definition found in the schema."
        | [elm] -> inferElementType (Ctx()) elm
        | elms -> 
            elms 
            |> List.map (fun elm -> 
                InferedTypeTag.Record (getElementName elm), inferElementType (Ctx()) elm)
            |> Map.ofList
            |> InferedType.Heterogeneous
