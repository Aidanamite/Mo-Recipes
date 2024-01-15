using HarmonyLib;
using HMLLibrary;
using RaftModLoader;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.UI;
using System.Text;
using UnityEngine.Experimental.Rendering;
using I2.Loc;
using Dummiesman;
using ALib;
using System.Globalization;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;


namespace MoRecipes
{
    public class RecipeLoadException : Exception { public RecipeLoadException(string message, Exception inner = null) : base(message, inner) { } }

    public class Main : Mod
    {
        public static ObjectList created = new ObjectList();
        public static Main instance;
        public const string recipeFolder = "mods/MoRecipes";
        public const string errorLog = "mods/MoRecipes.GenerationExceptions.log";
        public static List<CustomRecipe> recipes = new List<CustomRecipe>();
        public static Dictionary<string,Dictionary<string,string>> lang = new Dictionary<string, Dictionary<string, string>>();
        public static HashSet<Item_Base> createdFood = new HashSet<Item_Base>();
        public static GameObject FoodObjectPrefab;
        public const string DynamicFoodObjectName = "MoRecipes.CustomFood";
        public static Texture2D BasicRecipe;
        public static Texture2D SpecialRecipe;
        public static Transform PrefabParent;
        public static Material BasicGlassMaterial;
        public static Material GlassCupMaterial;
        public static Dictionary<CookingRecipeType, Dictionary<string, string>> RecipeTranslations = new Dictionary<CookingRecipeType, Dictionary<string, string>>();
        public static Dictionary<CookingRecipeType, (HashSet<int>, HashSet<string>)> AllCostItems = new Dictionary<CookingRecipeType, (HashSet<int>, HashSet<string>)>();
        Harmony harmony;
        public void Start()
        {
            foreach (var i in new[] { 0, 1, 2, 3, 4 }.RemoveFromArray(4))
                Debug.Log(i);
            instance = this;
            if (!Directory.Exists(recipeFolder))
                Directory.CreateDirectory(recipeFolder);
            PrefabParent = new GameObject("MoRecipes.PrefabParent").transform;
            PrefabParent.gameObject.SetActive(false);
            DontDestroyOnLoad(PrefabParent.gameObject);
            created.Add(PrefabParent.gameObject);
            BasicRecipe = LoadImage("basic_recipe.png", false, embeddedFile: true);
            SpecialRecipe = LoadImage("special_recipe.png", false, embeddedFile: true);
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m.name == "Glass")
                    BasicGlassMaterial = m;
                else if (m.name == "DrinkingGlass")
                    GlassCupMaterial = m;
            (harmony = new Harmony("com.aidanamite.MoRecipes")).PatchAll();
            GenerateRecipeTranslations();
            if (Directory.Exists(recipeFolder))
                foreach (var f in Directory.GetFiles(recipeFolder, "*.json", SearchOption.AllDirectories))
                    foreach (var r in ReadAll(f))
                        if (r != null)
                            if (!r.TryInitialize())
                                Debug.LogError($"Failed to initialize recipe {r.UniqueName} from {f}");
            Log("Mod has been loaded!");
        }

        public void OnModUnload()
        {
            foreach (var o in recipes)
                o.Destroy();
            foreach (var o in created)
                if (o)
                    Destroy(o);
            harmony?.UnpatchAll(harmony.Id);
            Log("Mod has been unloaded!");
        }

        public static void GenerateRecipeTranslations()
        {
            RecipeTranslations.Clear();
            var langs = LocalizationManager.GetAllLanguages();
            foreach (var r in ItemManager.GetAllItems())
                if (r && r.settings_buildable.HasBuildablePrefabs && !string.IsNullOrEmpty(r.settings_Inventory.LocalizationTerm))
                {
                    var ui = r.settings_buildable.GetBlockPrefab(0).GetComponent<CookingTable_Recipe_UI>();
                    if (ui && ui.Recipe && ui.Recipe.IsValid && !RecipeTranslations.TryGetValue(ui.Recipe.RecipeType,out var d))
                    {
                        RecipeTranslations[ui.Recipe.RecipeType] = d = new Dictionary<string, string>();
                        foreach (var l in langs)
                            d[LocalizationManager.GetLanguageCode(l)] = LocalizationManager.GetTermTranslation(r.settings_Inventory.LocalizationTerm, overrideLanguage: l).GetRecipeFormat();
                    }
                }
        }

        public static Texture2D CreateRecipeTexture(Sprite baseTex, bool special = false)
        {
            var n = Instantiate(special ? SpecialRecipe : BasicRecipe);
            created.Add(n);
            var temp = RenderTexture.GetTemporary((int)(n.width * 0.8), (int)(n.height * 0.8), 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            var area = baseTex.rect;
            Graphics.Blit(baseTex.texture, temp,new Vector2(area.width / baseTex.texture.width, area.height / baseTex.texture.height), new Vector2(area.x / baseTex.texture.width, area.y / baseTex.texture.height));
            var prev = RenderTexture.active;
            RenderTexture.active = temp;
            var temp2 = new Texture2D(temp.width,temp.height, n.format, false);
            temp2.ReadPixels(new Rect(0,0,temp.width,temp.height), 0, 0);
            var th = temp2.height;
            var tw = temp2.width;
            var np = temp2.GetPixels(0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(temp);
            Destroy(temp2);
            var p = n.GetPixels(0);
            var xo = 7;
            var yo = 2;
            for (int i = 0; i < p.Length; i++)
            {
                var x = i % n.width;
                var y = i / n.width;
                if (x >= xo && x < xo + tw && y >= yo && y < yo + th)
                    p[i] = p[i].Overlap(np[x - xo + (y - yo) * tw]);
                if (p[i].a == 0)
                    p[i] = new Color(0, 0, 0, 1 / 255f);
            }
            n.SetPixels(p, 0);
            n.Apply(true);
            return n;
        }

        public static List<CustomRecipe> ReadAll(string filename)
        {
            var folder = Path.GetDirectoryName(filename);
            var json = JSON.Parse(File.ReadAllText(filename));
            if (json.IsObject && json.TryGetValue("Recipes", out var col))
            {
                if (!col.IsCollection)
                    throw new RecipeLoadException("The recipe information is formatted incorrectly: \"Recipes\" must be a collection");
                var results = new List<CustomRecipe>();
                foreach (var child in col)
                    results.Add(GenerateRecipe(child,folder));
                return results;
            }
            return new List<CustomRecipe>() { GenerateRecipe(json, folder) };
        }

        public static void DegenerateRecipe(CustomRecipe recipe)
        {
            recipe.Destroy();
            if (recipe.Result_Icon)
                Destroy(recipe.Result_Icon);
            if (recipe.Result_Model)
                Destroy(recipe.Result_Model);
            if (recipe.Result_Materials != null)
                foreach (var m in recipe.Result_Materials)
                    if (m)
                        Destroy(m);

        }
        public static CustomRecipe GenerateRecipe(JSON json, string folder)
        {
            var r = new CustomRecipe();
            var err = new StringBuilder();
            if (!json.IsObject)
            {
                err.Append("\nRecipe data must be an object");
            }
            else
            {
                var tempCreate = created.GetTempChild();
                try
                {
                    // UniqueIndex
                    if (json.TryGetValue("UniqueIndex", out var c) && c.CanBeInteger && c.Integer > 0 && c.Integer <= short.MaxValue)
                        r.UniqueIndex = c.Integer;
                    else
                    {
                        err.Append("\n\"UniqueIndex\" must be a number between 0 and ");
                        err.Append(short.MaxValue + 1);
                    }

                    // UniqueName
                    if (json.TryGetValue("UniqueName", out c) && c.IsString)
                        r.UniqueName = c.String.EnsureStartsWith("Placeable_Recipe_");
                    else
                        err.Append("\n\"UniqueName\" must be a string");

                    // RecipeType
                    if (json.TryGetValue("RecipeType", out c) && c.TryAsEnum(out CookingRecipeType rt))
                        r.RecipeType = rt;
                    else
                        err.Append("\n\"RecipeType\" must be a valid cooking recipe type");

                    // Portions
                    if (json.TryGetValue("Portions", out c) && c.CanBeUInteger)
                        r.Portions = c.UInteger;
                    else
                        err.Append("\n\"Portions\" must be a number greater than 0");

                    // RecipeIndex
                    if (json.TryGetValue("RecipeIndex", out c) && c.CanBeUInteger)
                        r.RecipeIndex = c.UInteger;
                    else
                        err.Append("\n\"RecipeIndex\" must be a number greater than 0");

                    // CookTime
                    if (json.TryGetValue("CookTime", out c) && c.CanBeFloat)
                        r.CookTime = c.Float;
                    else
                        err.Append("\n\"CookTime\" must be a number");

                    // IsSpecial
                    if (json.TryGetValue("IsSpecial", out c))
                    {
                        if (c.CanBeBoolean)
                            r.IsSpecial = c.Boolean;
                        else
                            err.Append("\n\"IsSpecial\" must be true or false");
                    }

                    // Name
                    var rname = json.TryGetTranslations("Name", false, "\nRecipe ", err);
                    if (rname != null)
                        r.Name = rname;

                    // Cost
                    if (json.TryGetValue("Cost", out c) && c.IsCollection)
                    {
                        r.Cost = new ((string, int)[], int)[c.Count];
                        var i = 0;
                        var t = 0;
                        foreach (var j in c)
                        {
                            if (j.IsObject)
                            {
                                if (j.TryGetValue("Amount", out var v) && v.CanBeInteger)
                                    t += r.Cost[i].Amount = v.Integer;
                                else
                                    err.Append("\nCost \"Amount\" must be an number greater than 0");
                                if (j.TryGetValue("Items", out v))
                                {
                                    if (v.IsCollection)
                                    {
                                        r.Cost[i].Items = new (string, int)[v.Count];
                                        var l = 0;
                                        foreach (var k in v)
                                        {
                                            if (k.IsString)
                                                r.Cost[i].Items[l] = (k.String, -1);
                                            else if (k.CanBeInteger)
                                                r.Cost[i].Items[l] = (null, k.Integer);
                                            else
                                                err.Append("\nCost \"Items\" values must be either a number or string");
                                            l++;
                                        }
                                    }
                                    else if (v.IsString)
                                        r.Cost[i].Items = new[] { (v.String, -1) };
                                    else if (v.CanBeInteger)
                                        r.Cost[i].Items = new (string, int)[] { (null, v.Integer) };
                                    else
                                        err.Append("\nCost \"Items\" value must be either a number or string or a collection of such");
                                }
                                else if (j.TryGetValue("Item", out v))
                                {
                                    if (v.IsString)
                                        r.Cost[i].Items = new[] { (v.String, -1) };
                                    else if (v.CanBeInteger)
                                        r.Cost[i].Items = new (string, int)[] { (null, v.Integer) };
                                    else
                                        err.Append("\nCost \"Item\" value must be either a number or string");
                                }
                                else
                                    err.Append("\nCost \"Items\" value must be either a number or string or a collection of such");
                            }
                            else
                                err.Append("\nCost value must be an object that specifies item(s) and an amount");
                            i++;
                        }
                        if (t != 4)
                            err.Append("\nTotal items in \"Cost\" must equal to 4");
                    }
                    else
                        err.Append("\n\"Cost\" must be a collection");

                    // Result
                    if (json.TryGetValue("Result", out var ri))
                    {
                        if (ri.IsString)
                            r.Result_UniqueName = ri.String;
                        else if (ri.CanBeInteger)
                        {
                            if (ri.Integer > 0 && ri.Integer <= short.MaxValue)
                                r.Result_UniqueIndex = ri.Integer;
                            else
                            {
                                err.Append("\n\"Result\" as a number must be between 0 and ");
                                err.Append(short.MaxValue + 1);
                            }
                        }
                        else if (ri.IsObject)
                        {
                            // Result UniqueIndex
                            if (ri.TryGetValue("UniqueIndex", out c) && c.CanBeInteger && c.Integer > 0 && c.Integer <= short.MaxValue)
                                r.Result_UniqueIndex = c.Integer;
                            else
                            {
                                err.Append("\nResult \"UniqueIndex\" must be a number between 0 and ");
                                err.Append(short.MaxValue + 1);
                            }

                            // Result UniqueName
                            if (ri.TryGetValue("UniqueName", out c) && c.IsString)
                                r.Result_UniqueName = c.String;
                            else
                                err.Append("\nResult \"UniqueName\" must be a string");

                            // Result Icon
                            if (ri.TryGetValue("Icon", out c) && c.IsString)
                            {
                                if (File.Exists(Path.Combine(folder, c.String)))
                                    try
                                    {
                                        r.Result_Icon = LoadImage(Path.Combine(folder, c.String), false);
                                    }
                                    catch
                                    {
                                        err.Append("\nResult \"Icon\" failed to load");
                                    }
                                else
                                {
                                    err.Append("\nResult \"Icon\" file \"");
                                    err.Append(Path.Combine(folder, c.String));
                                    err.Append("\" not found");
                                }
                            }
                            else
                                err.Append("\nResult \"Icon\" must be a string");

                            // Result Model
                            if (ri.TryGetValue("Model", out c) && c.IsString)
                            {
                                var models = new OBJLoader().Load(Path.Combine(folder, c.String));
                                if (models.Count > 0)
                                {
                                    for (int i = 1; i < models.Count; i++)
                                        Destroy(models[i]);
                                    created.Add(models[0]);
                                    r.Result_Model = models[0];
                                }
                                else
                                {
                                    err.Append("\nResult model file \"");
                                    err.Append(c.String);
                                    err.Append("\" contains no model");
                                }
                            }
                            else
                                err.Append("\nResult \"Model\" must be a string");

                            // Result Material/Materials
                            if (ri.TryGetValue("Materials", out c))
                            {
                                if (c.IsCollection)
                                {
                                    r.Result_Materials = new Material[c.Count];
                                    var j = 0;
                                    foreach (var i in c)
                                        if (i.IsString)
                                            r.Result_Materials[j++] = CreateMaterial(i, folder, err);
                                        else
                                            err.Append("\nResult \"Materials\" values must be strings");
                                }
                                else if (c.IsString)
                                    r.Result_Materials = new[] { CreateMaterial(c, folder, err) };
                                else
                                    err.Append("\nResult \"Materials\" value must be a string");
                            }
                            else if (ri.TryGetValue("Material", out c) && c.IsString)
                                r.Result_Materials = new[] { CreateMaterial(c, folder, err) };
                            else
                                err.Append("\nResult \"Material\" value must be a string");

                            // Result FoodType
                            if (ri.TryGetValue("FoodType", out c) && c.TryAsEnum(out FoodType ft))
                                r.Result_FoodType = ft;
                            else
                                err.Append("\nResult \"FoodType\" must be a valid food type");

                            // Result FoodForm
                            if (ri.TryGetValue("FoodForm", out c) && c.TryAsEnum(out FoodForm f2))
                                r.Result_FoodForm = f2;
                            else
                                err.Append("\nResult \"FoodForm\" must be a valid food type");

                            // Result Oxygen
                            if (ri.TryGetValue("Oxygen", out c))
                            {
                                if (c.CanBeFloat)
                                    r.Result_Oxygen = c.Float;
                                else
                                    err.Append("\nResult \"Oxygen\" must be a number");
                            }

                            // Result Food
                            if (ri.TryGetValue("Food", out c))
                            {
                                if (c.CanBeFloat)
                                    r.Result_Food = c.Float;
                                else
                                    err.Append("\nResult \"Food\" must be a number");
                            }

                            // Result FoodBonus
                            if (ri.TryGetValue("FoodBonus", out c))
                            {
                                if (c.CanBeFloat)
                                    r.Result_FoodBonus = c.Float;
                                else
                                    err.Append("\nResult \"FoodBonus\" must be a number");
                            }

                            // Result Water
                            if (ri.TryGetValue("Water", out c))
                            {
                                if (c.CanBeFloat)
                                    r.Result_Water = c.Float;
                                else
                                    err.Append("\nResult \"Water\" must be a number");
                            }

                            // Result WaterBonus
                            if (ri.TryGetValue("WaterBonus", out c))
                            {
                                if (c.CanBeFloat)
                                    r.Result_WaterBonus = c.Float;
                                else
                                    err.Append("\nResult \"WaterBonus\" must be a number");
                            }

                            //Result Name
                            var name = ri.TryGetTranslations("Name", true, "\nResult ", err);
                            if (name != null)
                                r.Result_Name = name;
                        }
                        else
                            err.Append("\"Result\" must be a UniqueName, UniqueIndex or new item information as an object");
                    }
                }
                catch (Exception e)
                {
                    var l = File.Exists(errorLog) ? File.ReadAllLines(errorLog, Encoding.UTF8).Length : 0;
                    err.Append("\nAn uncaught exception occured while generating the recipe. Ref number: @");
                    err.Append(l);
                    File.AppendAllText(errorLog, $"[@{l}] >> {e}\n\n\n", Encoding.UTF8);
                }
                created.ReleaseTempChild();
                r.created = tempCreate;
            }
            if (err.Length > 0)
            {
                DegenerateRecipe(r);
                throw new RecipeLoadException("The recipe information is formatted incorrectly:" + err.ToString());
            }
            recipes.Add(r);
            return r;
        }

        [ConsoleCommand("spawnObjModel")]
        public static string MyCommand(string[] args)
        {
            var meshes = new OBJLoader().Load(args[0]);
            var mat = CreateBasicMaterial(LoadImage(args[1]));
            created.AddRange(meshes);
            foreach (var i in meshes)
            {
                var g = new GameObject("");
                created.Add(g);
                g.AddComponent<MeshFilter>().sharedMesh = i;
                g.AddComponent<MeshRenderer>().sharedMaterial = mat;
                g.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 3;
            }
            return "Created";
        }

        public static Material CreateMaterial(JSON json, string folder, StringBuilder errors)
        {
            if (json.IsString)
            {
                var s = json.String;
                if (s.ToLowerInvariant() == "glass")
                    return BasicGlassMaterial;
                if (s.ToLowerInvariant() == "glasscup")
                    return GlassCupMaterial;
                s = Path.Combine(folder, s);
                if (!File.Exists(s))
                {
                    errors.Append("\nMaterial texture \"");
                    errors.Append(s);
                    errors.Append("\" not found");
                    return null;
                }
                var m = new Material(Shader.Find("Standard"));
                created.Add(m);
                try
                {
                    m.name = (m.mainTexture = LoadImage(s)).name;
                } catch
                {
                    errors.Append("\nMaterial texture \"");
                    errors.Append(s);
                    errors.Append("\" failed to load");
                    return null;
                }
                return m;
            }
            if (json.IsObject)
            {
                Shader shader = null;
                Material m = null;
                if (json.TryGetValue("Shader", out var sh))
                {
                    if (sh.IsString)
                    {
                        shader = sh.String == "Glass" ? Shader.Find("GlassWater") : Shader.Find(sh.String);
                        if (!shader)
                        {
                            errors.Append("\nShader \"");
                            errors.Append(sh.String);
                            errors.Append("\" not found");
                            return null;
                        }
                    }
                    else
                    {
                        errors.Append("\nShader value must be a string");
                        return null;
                    }
                }
                else if (json.TryGetValue("Material", out sh))
                {
                    if (sh.IsString)
                    {
                        var str = sh.String;
                        var lstr = str.ToLowerInvariant();
                        m = lstr == "glass" ? BasicGlassMaterial : lstr == "glasscup" ? GlassCupMaterial : Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(x => x.name == str);
                        if (!m)
                        {
                            errors.Append("\nMaterial \"");
                            errors.Append(sh.String);
                            errors.Append("\" not found");
                            return null;
                        }
                    }
                    else
                    {
                        errors.Append("\nMaterial value must be a string");
                        return null;
                    }
                }
                else
                    shader = Shader.Find("Standard");
                if (shader)
                    m = new Material(shader);
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var name = shader.GetPropertyName(i);
                    if (json.TryGetValue(name, out var p))
                    {
                        var t = shader.GetPropertyType(i);
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Color)
                        {
                            var r = p.TryGetColor("\nColor property " + name, errors);
                            if (r != null)
                                m.SetColor(name, r.Value);
                            continue;
                        }
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Float)
                        {
                            if (p.CanBeFloat)
                            {
                                m.SetFloat(name, p.Float);
                                continue;
                            }
                            errors.Append("\nFloat property ");
                            errors.Append(name);
                            errors.Append(" is not a valid float value");
                        }
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Vector)
                        {
                            var r = p.TryGetVector("\nVector property " + name, errors);
                            if (r != null)
                                m.SetVector(name, r.Value);
                            continue;
                        }
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Range)
                        {
                            if (p.IsArray)
                            {
                                {
                                    var a = m.GetColorArray(name);
                                    if (a != null)
                                    {
                                        for (int j = 0; j < a.Length; j++)
                                        {
                                            var r = p.TryGetColor($"\nColor array property {name}[{j}]", errors);
                                            if (r != null)
                                                a[j] = r.Value;
                                        }
                                        m.SetColorArray(name, a);
                                        continue;
                                    }
                                }
                                {
                                    var a = m.GetVectorArray(name);
                                    if (a != null)
                                    {
                                        for (int j = 0; j < a.Length; j++)
                                        {
                                            var r = p.TryGetVector($"\nVector array property {name}[{j}]", errors);
                                            if (r != null)
                                                a[j] = r.Value;
                                        }
                                        m.SetVectorArray(name, a);
                                        continue;
                                    }
                                }
                                {
                                    var a = m.GetFloatArray(name);
                                    if (a != null)
                                    {
                                        for (int j = 0; j < a.Length; j++)
                                        {
                                            if (p.CanBeFloat)
                                            {
                                                a[j] = p.Float;
                                                continue;
                                            }
                                            errors.Append("\nFloat array property ");
                                            errors.Append(name);
                                            errors.Append("[");
                                            errors.Append(j);
                                            errors.Append("] is not a valid float value");
                                            continue;
                                        }
                                        m.SetFloatArray(name, a);
                                        continue;
                                    }
                                }
                                errors.Append("\nMatrix array property ");
                                errors.Append(name);
                                errors.Append(" cannot be modified");
                                continue;
                            }
                            errors.Append("\nArray property ");
                            errors.Append(name);
                            errors.Append(" value must be an array");
                            continue;
                        }
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        {
                            if (p.IsString)
                            {
                                var s = Path.Combine(folder, p.String);
                                if (!File.Exists(s))
                                {
                                    errors.Append("\nTexture property ");
                                    errors.Append(name);
                                    errors.Append(" value \"");
                                    errors.Append(s);
                                    errors.Append("\" file could not be found");
                                }
                                m.SetTexture(name,LoadImage(s));
                                continue;
                            }
                            if (p.IsObject)
                            {
                                if (!p.TryGetValue("File",out var j) && !p.TryGetValue("Texture", out j))
                                {
                                    errors.Append("\nTexture property ");
                                    errors.Append(name);
                                    errors.Append(" must have a \"file\" value");
                                    continue;
                                }
                                if (!j.IsString)
                                {
                                    errors.Append("\nTexture property ");
                                    errors.Append(name);
                                    errors.Append(" file value must be a string");
                                    continue;
                                }
                                var s = Path.Combine(folder, p.String);
                                if (!File.Exists(s))
                                {
                                    errors.Append("\nTexture property ");
                                    errors.Append(name);
                                    errors.Append(" file value \"");
                                    errors.Append(s);
                                    errors.Append("\" could not be found");
                                }
                                m.SetTexture(name, LoadImage(s));
                                if (p.TryGetValue("Scale",out j))
                                {
                                    var v = j.TryGetVector($"\nTexture property {name} scale value", errors);
                                    if (v != null)
                                        m.SetTextureScale(name, v.Value);
                                }
                                if (p.TryGetValue("Offset", out j))
                                {
                                    var v = j.TryGetVector($"\nTexture property {name} offset value", errors);
                                    if (v != null)
                                        m.SetTextureOffset(name, v.Value);
                                }
                                continue;
                            }
                            errors.Append("\nTexture property ");
                            errors.Append(name);
                            errors.Append(" is not a valid value");
                        }
                    }
                }
                return m;
            }
            errors.Append("\nMaterial is not a valid value");
            return null;
        }

        public static Material CreateBasicMaterial(Texture texture)
        {
            var m = new Material(Shader.Find("Standard"));
            created.Add(m);
            m.mainTexture = texture;
            m.name = texture.name;
            return m;
        }

        public static Texture2D LoadImage(string filename, bool mipMaps = true, FilterMode? mode = null, bool leaveReadable = true, bool embeddedFile = false)
        {
            var t = new Texture2D(0, 0, TextureFormat.BGRA32, mipMaps);
            created.Add(t);
            t.LoadImage(embeddedFile ? instance.GetEmbeddedFileBytes(filename) : File.ReadAllBytes(filename),!leaveReadable);
            t.name = filename;
            if (mode != null)
                t.filterMode = mode.Value;
            return t;
        }

        static FieldInfo _allRecipes = typeof(CookingTable).GetField("allRecipes", ~BindingFlags.Default);
        public static SO_CookingTable_Recipe[] GetAllRecipes() => (SO_CookingTable_Recipe[])_allRecipes.GetValue(null);
        public static void SetAllRecipes(SO_CookingTable_Recipe[] value) => _allRecipes.SetValue(null, value);
    }

    public class CustomRecipe
    {
        public string FromFile;
        public string UniqueName;
        public int UniqueIndex;
        public CookingRecipeType RecipeType;
        public uint RecipeIndex;
        public uint Portions;
        public float CookTime;
        public bool IsSpecial;
        public Dictionary<string, string> Name;
        public ((string, int)[] Items, int Amount)[] Cost;

        public string Result_UniqueName;
        public int Result_UniqueIndex;
        public Texture2D Result_Icon;
        public Mesh Result_Model;
        public Material[] Result_Materials;
        public FoodType Result_FoodType;
        public FoodForm Result_FoodForm;
        public PlayerAnimation? AnimationOnUse;
        public PlayerAnimation? AnimationOnSelect;
        public string Result_ConsumeSound;
        public float Result_Food;
        public float Result_FoodBonus;
        public float Result_Water;
        public float Result_WaterBonus;
        public float Result_Oxygen;
        public Dictionary<string,string> Result_Name;


        public Item_Base ResultItem;
        public Item_Base RecipeItem;
        public SO_CookingTable_Recipe SO;

        public ObjectList.ChildList created;

        public bool TryInitialize()
        {
            Main.created.ReinstateTempChild(created);
            try
            {
                if (!ResultItem)
                {
                    if (Result_Icon)
                    {
                        var item = RecipeType == CookingRecipeType.Juicer ? ItemManager.GetItemByName("DrinkingGlass_SimpleSmoothie") : ItemManager.GetItemByName("ClayPlate_SharkDinner");
                        var nItem = item.Clone(Result_UniqueIndex, Result_UniqueName);
                        nItem.settings_consumeable = new ItemInstance_Consumeable(Result_Food, Result_FoodBonus, Result_Water, Result_WaterBonus, false, item.settings_consumeable.ItemAfterUse, Result_FoodType, Result_FoodForm);
                        nItem.settings_consumeable.SetConsumeSound(Result_ConsumeSound);
                        nItem.settings_Inventory.LocalizationTerm = "MoRecipes/Items/" + nItem.UniqueName;
                        if (AnimationOnUse != null)
                            nItem.settings_usable.SetAnimationOnUse(AnimationOnUse.Value);
                        if (AnimationOnSelect != null)
                            nItem.settings_usable.SetAnimationOnSelect(AnimationOnSelect.Value);
                        Main.created.Add(nItem.settings_Inventory.Sprite = Sprite.Create(Result_Icon, new Rect(0, 0, Result_Icon.width, Result_Icon.height), new Vector2(0.5f, 0.5f)));
                        Main.lang[nItem.settings_Inventory.LocalizationTerm] = Result_Name;
                        RAPI.RegisterItem(ResultItem = nItem);
                    }
                    else if (!string.IsNullOrEmpty(Result_UniqueName))
                        ResultItem = ItemManager.GetItemByName(Result_UniqueName);
                    else
                        ResultItem = ItemManager.GetItemByIndex(Result_UniqueIndex);
                    if (!ResultItem)
                    {
                        Debug.LogWarning($"Failed to find result item {(string.IsNullOrEmpty(Result_UniqueName) ? Result_UniqueIndex.ToString() : $"\"{Result_UniqueName}\"")}");
                        return false;
                    }
                }
                if (!RecipeItem)
                {
                    var item = ItemManager.GetItemByName("Placeable_Recipe_VegetableSoup");
                    var nItem = item.Clone(UniqueIndex, UniqueName);
                    nItem.settings_Inventory.LocalizationTerm = "MoRecipes/Items/" + nItem.UniqueName;
                    if (Name == null)
                    {
                        var lang = Main.lang[nItem.settings_Inventory.LocalizationTerm] = new Dictionary<string, string>();
                        if (Main.RecipeTranslations.TryGetValue(RecipeType, out var d))
                            foreach (var l in d)
                                lang[l.Key] = string.Format(l.Value, ResultItem.settings_Inventory.LocalizationTerm == null ? ResultItem.settings_Inventory.DisplayName : LocalizationManager.GetTermTranslation(ResultItem.settings_Inventory.LocalizationTerm, overrideLanguage: LocalizationManager.GetLanguageFromCode( l.Key)));
                        Main.lang[nItem.settings_Inventory.LocalizationTerm] = lang;
                    }
                    else
                        Main.lang[nItem.settings_Inventory.LocalizationTerm] = Name;
                    var t = Main.CreateRecipeTexture(ResultItem.settings_Inventory.Sprite, IsSpecial);
                    Main.created.Add(nItem.settings_Inventory.Sprite = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f)));
                    var p = nItem.settings_buildable.GetBlockPrefabs().ToArray();
                    for (int i = 0; i < p.Length; i++)
                    {
                        p[i] = Object.Instantiate(p[i], Main.PrefabParent, false);
                        Main.created.Add(p[i]);
                        p[i].name = nItem.UniqueName;
                        p[i].ReplaceValues(item, nItem);
                    }
                    nItem.settings_buildable.SetBlockPrefabs(p);
                    RAPI.RegisterItem(RecipeItem = nItem);
                    Traverse.Create(typeof(LocalizationManager)).Field("OnLocalizeEvent").GetValue<LocalizationManager.OnLocalizeCallback>().Invoke();
                    foreach (var q in Resources.FindObjectsOfTypeAll<SO_BlockQuadType>())
                        if (q.AcceptsBlock(item))
                            Traverse.Create(q).Field("acceptableBlockTypes").GetValue<List<Item_Base>>().Add(nItem);
                    foreach (var q in Resources.FindObjectsOfTypeAll<SO_BlockCollisionMask>())
                        if (q.IgnoresBlock(item))
                            Traverse.Create(q).Field("blockTypesToIgnore").GetValue<List<Item_Base>>().Add(nItem);
                }
                if (!SO)
                {
                    var cost = new CostMultiple[Cost.Length];
                    for (int i = 0; i < cost.Length; i++)
                    {
                        var find = Cost[i].Items;
                        var items = new Item_Base[find.Length];
                        for (int j = 0; j < items.Length; j++)
                        {
                            items[j] = string.IsNullOrEmpty(find[j].Item1) ? ItemManager.GetItemByIndex(find[j].Item2) : ItemManager.GetItemByName(find[j].Item1);
                            if (!items[j])
                            {
                                Debug.LogWarning($"Failed to find cost item {(string.IsNullOrEmpty(find[j].Item1) ? find[j].Item2.ToString() : $"\"{find[j].Item1}\"")}");
                                return false;
                            }
                        }
                        cost[i] = new CostMultiple(items, Cost[i].Amount);
                    }
                    SO = ScriptableObject.CreateInstance<SO_CookingTable_Recipe>();
                    SO.SetCookTime(CookTime);
                    SO.SetIsBuff(IsSpecial);
                    SO.SetPortions(Portions);
                    SO.SetRecipeCost(cost);
                    SO.SetRecipeIndex(RecipeIndex);
                    SO.SetRecipeType(RecipeType);
                    SO.SetResult(ResultItem);
                    foreach (var p in RecipeItem.settings_buildable.GetBlockPrefabs())
                        p.GetComponent<CookingTable_Recipe_UI>().SetRecipe(SO);
                    Main.SetAllRecipes(Main.GetAllRecipes().AddToArray(SO));
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
            finally
            {
                Main.created.ReleaseTempChild();
            }
            return true;
        }

        public void Destroy()
        {
            if (SO)
            {
                Main.SetAllRecipes(Main.GetAllRecipes().RemoveFromArray(SO));
                Object.Destroy(SO);
            }
            if (RecipeItem)
            {
                foreach (var q in Resources.FindObjectsOfTypeAll<SO_BlockQuadType>())
                    Traverse.Create(q).Field("acceptableBlockTypes").GetValue<List<Item_Base>>().Remove(RecipeItem);
                foreach (var q in Resources.FindObjectsOfTypeAll<SO_BlockCollisionMask>())
                    Traverse.Create(q).Field("blockTypesToIgnore").GetValue<List<Item_Base>>().Remove(RecipeItem);
                foreach (var b in BlockCreator.GetPlacedBlocks())
                    if (b && b.buildableItem == RecipeItem)
                        BlockCreator.RemoveBlock(b, null, true);
                RAPI.UnregisterItem(RecipeItem);
                Main.lang.Remove(RecipeItem.settings_Inventory.LocalizationTerm);
                Object.Destroy(RecipeItem);
            }
            if (Result_Icon && ResultItem)
            {
                RAPI.UnregisterItem(ResultItem);
                Main.lang.Remove(ResultItem.settings_Inventory.LocalizationTerm);
                Object.Destroy(ResultItem);
            }
            if (created != null)
                foreach (var i in created)
                    if (i)
                        Object.Destroy(i);
            /*
            foreach (var r in Main.recipes)
                if (r.RecipeType == tableType)
                    foreach (var c in r.Cost)
                        foreach (var i in c.Items)
                            if (i.Item1 == item.UniqueName || i.Item2 == item.UniqueIndex)
                            {
                                __result = true;
                                return;
                            }*/
        }
    }

    public class CustomConsumeComponent : ConsumeComponent
    {
        public MeshRenderer renderer;
        public MeshFilter filter;
        public void OnSelect()
        {
            var item = GetComponentInParent<Network_Player>().Inventory.GetSelectedHotbarItem();
            if (item != null && item.baseItem && Main.createdFood.Contains(item.baseItem))
            {
                var i = Main.recipes.First(x => x.Result_UniqueIndex == item.UniqueIndex && x.Result_Icon);
                filter.sharedMesh = i.Result_Model;
                renderer.sharedMaterials = i.Result_Materials;
            }
            else
            {
                Debug.LogWarning("MoRecipes.CustomConsumeComponent triggered on item model not created by MoRecipes");
                filter.sharedMesh = null;
                renderer.sharedMaterials = Array.Empty<Material>();
            }
        }
    }

    public static class ExtentionMethods
    {
        static FieldInfo _isBuff = typeof(SO_CookingTable_Recipe).GetField("isBuff", ~BindingFlags.Default);
        static FieldInfo _recipeIndex = typeof(SO_CookingTable_Recipe).GetField("recipeIndex", ~BindingFlags.Default);
        static FieldInfo _result = typeof(SO_CookingTable_Recipe).GetField("result", ~BindingFlags.Default);
        static FieldInfo _portions = typeof(SO_CookingTable_Recipe).GetField("portions", ~BindingFlags.Default);
        static FieldInfo _cookTime = typeof(SO_CookingTable_Recipe).GetField("cookTime", ~BindingFlags.Default);
        static FieldInfo _recipeCost = typeof(SO_CookingTable_Recipe).GetField("recipeCost", ~BindingFlags.Default);
        static FieldInfo _recipeType = typeof(SO_CookingTable_Recipe).GetField("recipeType", ~BindingFlags.Default);
        static FieldInfo _recipe = typeof(CookingTable_Recipe_UI).GetField("recipe", ~BindingFlags.Default);
        static FieldInfo _animationOnUse = typeof(ItemInstance_Usable).GetField("animationOnUse", ~BindingFlags.Default);
        static FieldInfo _animationOnSelect = typeof(ItemInstance_Usable).GetField("animationOnSelect", ~BindingFlags.Default);
        static FieldInfo _blockPrefabs = typeof(ItemInstance_Buildable).GetField("blockPrefabs", ~BindingFlags.Default);
        public static void SetIsBuff(this SO_CookingTable_Recipe so, bool value) => _isBuff.SetValue(so, value);
        public static void SetRecipeIndex(this SO_CookingTable_Recipe so, uint value) => _recipeIndex.SetValue(so, value);
        public static void SetResult(this SO_CookingTable_Recipe so, Item_Base value) => _result.SetValue(so, value);
        public static void SetPortions(this SO_CookingTable_Recipe so, uint value) => _portions.SetValue(so, value);
        public static void SetCookTime(this SO_CookingTable_Recipe so, float value) => _cookTime.SetValue(so, value);
        public static void SetRecipeCost(this SO_CookingTable_Recipe so, CostMultiple[] value) => _recipeCost.SetValue(so, value);
        public static void SetRecipeType(this SO_CookingTable_Recipe so, CookingRecipeType value) => _recipeType.SetValue(so, value);
        public static void SetRecipe(this CookingTable_Recipe_UI ui, SO_CookingTable_Recipe value) => _recipe.SetValue(ui, value);
        public static void SetAnimationOnUse(this ItemInstance_Usable iiu, PlayerAnimation value) => _animationOnUse.SetValue(iiu, value);
        public static void SetAnimationOnSelect(this ItemInstance_Usable iiu, PlayerAnimation value) => _animationOnSelect.SetValue(iiu, value);
        public static void SetBlockPrefabs(this ItemInstance_Buildable iib, Block[] value) => _blockPrefabs.SetValue(iib, value);


        static FieldInfo _eventRef_consumeSound = typeof(ItemInstance_Consumeable).GetField("eventRef_consumeSound", ~BindingFlags.Default);
        public static void SetConsumeSound(this ItemInstance_Consumeable consumable, string value) => _eventRef_consumeSound.SetValue(consumable, value);

        public static Item_Base Clone(this Item_Base source, int uniqueIndex, string uniqueName)
        {
            Item_Base item = ScriptableObject.CreateInstance<Item_Base>();
            item.Initialize(uniqueIndex, uniqueName, source.MaxUses);
            item.settings_buildable = source.settings_buildable.Clone();
            item.settings_consumeable = source.settings_consumeable.Clone();
            item.settings_cookable = source.settings_cookable.Clone();
            item.settings_equipment = source.settings_equipment.Clone();
            item.settings_Inventory = source.settings_Inventory.Clone();
            item.settings_recipe = source.settings_recipe.Clone();
            item.settings_usable = source.settings_usable.Clone();
            Main.created.Add(item);
            return item;
        }

        public static string EnsureStartsWith(this string original, string startsWith) => original.StartsWith(startsWith) ? original : (startsWith + original);

        public static Texture2D GetReadable(this Texture2D source, GraphicsFormat targetFormat, bool mipChain = true, Rect? copyArea = null, RenderTextureFormat format = RenderTextureFormat.Default, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default) =>
            source.CopyTo(
                new Texture2D(
                    (int)(copyArea?.width ?? source.width),
                    (int)(copyArea?.height ?? source.height),
                    targetFormat,
                    mipChain ? TextureCreationFlags.MipChain : TextureCreationFlags.None),
                copyArea,
                format,
                readWrite);

        public static Texture2D GetReadable(this Texture2D source, TextureFormat? targetFormat = null, bool mipChain = true, Rect? copyArea = null, RenderTextureFormat format = RenderTextureFormat.Default, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default) =>
            source.CopyTo(
                new Texture2D(
                    (int)(copyArea?.width ?? source.width),
                    (int)(copyArea?.height ?? source.height),
                    targetFormat ?? TextureFormat.ARGB32,
                    mipChain),
                copyArea,
                format,
                readWrite);

        static Texture2D CopyTo(this Texture2D source, Texture2D texture, Rect? copyArea = null, RenderTextureFormat format = RenderTextureFormat.Default, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default)
        {
            var temp = RenderTexture.GetTemporary(source.width, source.height, 0, format, readWrite);
            Graphics.Blit(source, temp);
            temp.filterMode = FilterMode.Point;
            var prev = RenderTexture.active;
            RenderTexture.active = temp;
            var area = copyArea ?? new Rect(0, 0, temp.width, temp.height);
            texture.ReadPixels(area, 0, 0);
            texture.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(temp);
            Main.created.Add(texture);
            return texture;
        }

        public static Color Overlap(this Color b, Color o)
        {
            if (o.a == 0)
                return b;
            if (b.a == 0)
                return o;
            var ba = b.a * (1 - o.a);
            var br = ba / (ba + o.a);
            var or = o.a / (ba + o.a);
            return new Color(b.r * br + o.r * or, b.g * br + o.g * or, b.b * br + o.b * or, ba + o.a);
        }

        public static void ReplaceValues(this Component value, object original, object replacement)
        {
            foreach (var c in value.GetComponentsInChildren<Component>())
                (c as object).ReplaceValues(original, replacement);
        }
        public static void ReplaceValues(this GameObject value, object original, object replacement)
        {
            foreach (var c in value.GetComponentsInChildren<Component>())
                (c as object).ReplaceValues(original, replacement);
        }

        public static void ReplaceValues(this object value, object original, object replacement)
        {
            var t = value.GetType();
            while (t != typeof(Object) && t != typeof(object))
            {
                foreach (var f in t.GetFields(~BindingFlags.Default))
                    if (!f.IsStatic && f.GetValue(value) == original)
                        f.SetValue(value, replacement);
                t = t.BaseType;
            }
        }
        public static T ReplaceComponent<T>(this Component original, int serializationLayers = 0) where T : Component
        {
            var g = original.gameObject;
            var n = g.AddComponent<T>();
            n.CopyFieldsOf(original);
            n.CopyPropertiesOf(original);
            g.ReplaceValues(original, n, serializationLayers);
            Object.DestroyImmediate(original);
            return n;
        }

        public static void CopyFieldsOf(this object value, object source)
        {
            var t1 = value.GetType();
            var t2 = source.GetType();
            while (!t1.IsAssignableFrom(t2))
                t1 = t1.BaseType;
            while (t1 != typeof(Object) && t1 != typeof(object))
            {
                foreach (var f in t1.GetFields(~BindingFlags.Default))
                    if (!f.IsStatic)
                        f.SetValue(value, f.GetValue(source));
                t1 = t1.BaseType;
            }
        }

        public static void CopyPropertiesOf(this object value, object source)
        {
            var t1 = value.GetType();
            var t2 = source.GetType();
            while (!t1.IsAssignableFrom(t2))
                t1 = t1.BaseType;
            while (t1 != typeof(Object) && t1 != typeof(object))
            {
                foreach (var p in t1.GetProperties(~BindingFlags.Default))
                    if (p.GetGetMethod() != null && p.GetSetMethod() != null && !p.GetGetMethod().IsStatic)
                        p.SetValue(value, p.GetValue(source));
                t1 = t1.BaseType;
            }
        }
        public static void ReplaceValues(this Component value, object original, object replacement, int serializableLayers = 0)
        {
            foreach (var c in value.GetComponentsInChildren<Component>(true))
                (c as object).ReplaceValues(original, replacement, serializableLayers);
        }
        public static void ReplaceValues(this GameObject value, object original, object replacement, int serializableLayers = 0)
        {
            foreach (var c in value.GetComponentsInChildren<Component>(true))
                (c as object).ReplaceValues(original, replacement, serializableLayers);
        }

        public static void ReplaceValues(this object value, object original, object replacement, int serializableLayers = 0)
        {
            if (value == null)
                return;
            var t = value.GetType();
            while (t != typeof(Object) && t != typeof(object))
            {
                foreach (var f in t.GetFields(~BindingFlags.Default))
                    if (!f.IsStatic)
                    {
                        if (f.GetValue(value) == original || (f.GetValue(value)?.Equals(original) ?? false))
                            try
                            {
                                f.SetValue(value, replacement);
                            }
                            catch { }
                        else if (f.GetValue(value) is IList)
                        {
                            var l = f.GetValue(value) as IList;
                            for (int i = 0; i < l.Count; i++)
                                if (l[i] == original || (l[i]?.Equals(original) ?? false))
                                    try
                                    {
                                        l[i] = replacement;
                                    }
                                    catch { }

                        }
                        else if (serializableLayers > 0 && (f.GetValue(value)?.GetType()?.IsSerializable ?? false))
                            f.GetValue(value).ReplaceValues(original, replacement, serializableLayers - 1);
                    }
                t = t.BaseType;
            }
        }

        public static Color? TryGetColor(this JSON json, string errorPrefix = null, StringBuilder errors = null)
        {
            if (json.IsString)
            {
                var s = json.String;
                var hash = s.StartsWith("#");
                if (!hash)
                {
                    var f = typeof(Color).GetProperty(s.ToLowerInvariant(), typeof(Color));
                    if (f != null)
                        return (Color)f.GetValue(null);
                }
                if (hash)
                    s = s.Remove(0, 1);
                if ((s.Length == 6 || s.Length == 8) && uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var r))
                {
                    var f = new Color32();
                    if (s.Length == 8)
                    {
                        f.a = (byte)(r % 256);
                        r /= 256;
                    }
                    f.b = (byte)(r % 256);
                    r /= 256;
                    f.g = (byte)(r % 256);
                    r /= 256;
                    f.r = (byte)r;
                    return f;
                }
                errors?.Append(errorPrefix);
                errors?.Append(" cannot use string \"");
                if (hash)
                    errors?.Append("#");
                errors?.Append(s);
                errors?.Append("\". Value is not a valid color");
                return null;
            }
            if (json.IsArray)
            {
                if (json.Count < 3)
                {
                    errors?.Append(errorPrefix);
                    errors?.Append(" needs at least 3 values");
                    return null;
                }
                if (json.Count > 4)
                {
                    errors?.Append(errorPrefix);
                    errors?.Append(" can have no more than 4 values");
                    return null;
                }
                var greater = false;
                var values = new float[4];
                for (var j = 0; j < json.Count; j++)
                {
                    var jv = json[j];
                    if (jv.CanBeFloat)
                    {
                        values[j] = jv.Float;
                        if (values[j] > 1)
                            greater = true;
                        if (values[j] > 255)
                        {
                            errors?.Append(errorPrefix);
                            errors?.Append(" value ");
                            errors?.Append(j);
                            errors?.Append(" cannot be greater than 255");
                            return null;
                        }
                        if (values[j] < 0)
                        {
                            errors?.Append(errorPrefix);
                            errors?.Append(" value ");
                            errors?.Append(j);
                            errors?.Append(" cannot be less than 0");
                            return null;
                        }
                    }
                    else
                    {
                        errors?.Append(errorPrefix);
                        errors?.Append(" value ");
                        errors?.Append(j);
                        errors?.Append(" is not a number");
                        return null;
                    }
                }
                if (json.Count == 3)
                    values[3] = greater ? 255 : 1;
                if (greater)
                    return new Color32((byte)values[0], (byte)values[1], (byte)values[2], (byte)values[3]);
                return new Color(values[0], values[1], values[2], values[3]);
            }
            if (json.IsObject)
            {
                var greater = false;
                var values = new float[4];
                var inds = new[] { "r","g","b","a" };
                var flags = 0;
                foreach (var p in (IDictionary<string, JSON>)json)
                {
                    var j = Array.IndexOf(inds, p.Key);
                    if (j >= 0)
                    {
                        flags |= 1 << j;
                        if (p.Value.CanBeFloat)
                        {
                            values[j] = p.Value.Float;
                            if (values[j] > 1)
                                greater = true;
                            if (values[j] > 255)
                            {
                                errors?.Append(errorPrefix);
                                errors?.Append(" value ");
                                errors?.Append(p.Key);
                                errors?.Append(" cannot be greater than 255");
                                return null;
                            }
                            if (values[j] < 0)
                            {
                                errors?.Append(errorPrefix);
                                errors?.Append(" value ");
                                errors?.Append(p.Key);
                                errors?.Append(" cannot be less than 0");
                                return null;
                            }
                        }
                        else
                        {
                            errors?.Append(errorPrefix);
                            errors?.Append(" value ");
                            errors?.Append(p.Key);
                            errors?.Append(" is not a number");
                            return null;
                        }
                    }
                }
                if (flags == 7)
                    values[3] = greater ? 255 : 1;
                else if (flags != 15)
                {
                    errors?.Append(errorPrefix);
                    errors?.Append(" must have at least r, g and b values");
                    return null;
                }
                if (greater)
                    return new Color32((byte)values[0], (byte)values[1], (byte)values[2], (byte)values[3]);
                return new Color(values[0], values[1], values[2], values[3]);
            }
            errors?.Append(errorPrefix);
            errors?.Append(" value is not a valid color value");
            return null;
        }

        public static Vector4? TryGetVector(this JSON json, string errorPrefix = null, StringBuilder errors = null)
        {
            if (json.IsArray)
            {
                if (json.Count > 4)
                {
                    errors?.Append(errorPrefix);
                    errors?.Append(" can have no more than 4 values");
                    return null;
                }
                var values = new float[4];
                for (var j = 0; j < json.Count; j++)
                {
                    var jv = json[j];
                    if (jv.CanBeFloat)
                    {
                        values[j] = jv.Float;
                        continue;
                    }
                    errors?.Append(errorPrefix);
                    errors?.Append(" value ");
                    errors?.Append(j);
                    errors?.Append(" is not a number");
                    return null;
                }
                return new Vector4(values[0], values[1], values[2], values[3]);
            }
            if (json.IsObject)
            {
                var values = new float[4];
                var inds = new[] { "x", "y", "z", "w" };
                foreach (var p in (IDictionary<string, JSON>)json)
                {
                    var j = Array.IndexOf(inds, p.Key);
                    if (j >= 0)
                    {
                        if (p.Value.CanBeFloat)
                        {
                            values[j] = p.Value.Float;
                            continue;
                        }
                        errors?.Append(errorPrefix);
                        errors?.Append(" value ");
                        errors?.Append(p.Key);
                        errors?.Append(" is not a number");
                        return null;
                    }
                }
                return new Vector4(values[0], values[1], values[2], values[3]);
            }
            errors?.Append(errorPrefix);
            errors?.Append(" value is not a valid vector value");
            return null;
        }
        public static string GetRecipeFormat(this string str) => str.Remove(str.IndexOf(": ") + 2) + "{0}" + str.Remove(0, str.IndexOf("@") + 1);
        public static Dictionary<string,string> TryGetTranslations(this JSON json, string name, bool required = true, string errorPrefix = null, StringBuilder errors = null)
        {
            if (json.TryGetValue(name, out var c))
            {
                if (c.IsObject)
                {
                    var d = new Dictionary<string, string>();
                    foreach (var k in c.Keys)
                    {
                        var v = json[k];
                        if (v.IsString)
                        {
                            d[k] = v.String;
                            continue;
                        }
                        errors.Append(errorPrefix);
                        errors.Append("\"");
                        errors.Append(name);
                        errors.Append("\" translation values must be a strings");
                    }
                    if (d.Count != 0)
                        return d;
                    errors.Append(errorPrefix);
                    errors.Append("\"");
                    errors.Append(name);
                    errors.Append("\" must contain at least one translation");
                    return null;
                }
                else if (c.IsString)
                    return new Dictionary<string, string> { ["en"] = c.String };
                else
                {
                    errors.Append(errorPrefix);
                    errors.Append("\"");
                    errors.Append(name);
                    errors.Append("\" must be a string or translations as an object");
                }
            }
            else if (required)
            {
                errors.Append(errorPrefix);
                errors.Append("\"");
                errors.Append(name);
                errors.Append("\" must be a string or translations as an object");
            }
            return null;
        }

        public static T[] RemoveFromArray<T>(this T[] a, T obj)
        {
            var ind = Array.IndexOf(a, obj);
            if (ind == -1)
                return a;
            if (a.Length == 1)
                return Array.Empty<T>();
            var n = new T[a.Length - 1];
            if (ind > 0)
                Array.Copy(a, n, ind);
            if (ind < n.Length)
                Array.Copy(a, ind + 1, n, ind, n.Length - ind);
            return n;
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), "TryGetTranslation")]
    static class Patch_Localization
    {
        static bool Prefix(string Term, ref string Translation, bool FixForRTL, int maxLineLengthForRTL, bool ignoreRTLnumbers, bool applyParameters, GameObject localParametersRoot, string overrideLanguage, ref bool __result)
        {
            if (Term != null && Main.lang.TryGetValue(Term, out var value))
            {
                var langCode = LocalizationManager.GetLanguageCode(overrideLanguage ?? LocalizationManager.CurrentLanguage);
                if (!(!string.IsNullOrEmpty(langCode) && value.TryGetValue(langCode, out Translation)) && !value.TryGetValue("en", out Translation))
                    Translation = value.FirstOrDefault().Value;
                if (applyParameters)
                    LocalizationManager.ApplyLocalizationParams(ref Translation, localParametersRoot, true);
                if (LocalizationManager.IsRight2Left && FixForRTL)
                    Translation = LocalizationManager.ApplyRTLfix(Translation, maxLineLengthForRTL, ignoreRTLnumbers);
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(UseItemController))]
    static class Patch_ItemController
    {
        [HarmonyPatch("GetItemNameFromUsable")]
        static bool Prefix(Item_Base item, ref string __result)
        {
            if (Main.createdFood.Contains(item))
            {
                __result = Main.DynamicFoodObjectName;
                return false;
            }
            return true;
        }

        [HarmonyPatch("Awake")]
        static void Postfix(Dictionary<string, ItemConnection> ___connectionDictionary)
        {
            if (!Main.FoodObjectPrefab)
            {
                Main.FoodObjectPrefab = Object.Instantiate(___connectionDictionary["ClayPlate_SharkDinner"].obj, Main.PrefabParent);
                var c = Main.FoodObjectPrefab.GetComponent<ConsumeComponent>().ReplaceComponent<CustomConsumeComponent>();
                c.filter = c.GetComponentInChildren<MeshFilter>(true);
                c.renderer = c.GetComponentInChildren<MeshRenderer>(true);
            }
            ___connectionDictionary[Main.DynamicFoodObjectName] = new ItemConnection() { name = Main.DynamicFoodObjectName, obj = Object.Instantiate(Main.FoodObjectPrefab, ___connectionDictionary["ClayPlate_SharkDinner"].obj.transform.parent) };
        }
    }

    [HarmonyPatch(typeof(ItemObjectEnabler), nameof(ItemObjectEnabler.DoesAcceptItem))]
    static class Patch_CookingTableAcceptItem
    {
        static void Postfix(ItemObjectEnabler __instance, Item_Base item, ref bool __result)
        {
            if (!item || !(__instance is ItemObjectEnabler_CookingTable))
                return;
            var tableType = __instance.GetComponentInParent<CookingTable>()?.cookingType;
            if (tableType == null)
                return;
            if (Main.AllCostItems.TryGetValue(tableType.Value, out var t) && (t.Item1.Contains(item.UniqueIndex) || t.Item2.Contains(item.UniqueName)))
                __result = true;
        }
    }

    public class ObjectList : IList<Object>
    {
        List<Object> objs = new List<Object>();
        List<Object> child;
        public ChildList GetTempChild() => new ChildList(child = new List<Object>());
        public void ReleaseTempChild() => child = null;
        public void ReinstateTempChild(ChildList list) => child = list.EXPOSE();
        public int IndexOf(Object obj) => objs.IndexOf(obj);
        public void Insert(int ind, Object obj)
        {
            child?.Add(obj);
            objs.Insert(ind, obj);
        }
        public void RemoveAt(int ind)
        {
            child?.Remove(objs[ind]);
            objs.RemoveAt(ind);
        }
        public Object this[int ind]
        {
            get => objs[ind];
            set
            {
                child?.Remove(objs[ind]);
                objs[ind] = value;
                child?.Add(value);
            }
        }
        public void Add(Object obj)
        {
            child?.Add(obj);
            objs.Add(obj);
        }
        public void AddRange(IEnumerable<Object> add)
        {
            child?.AddRange(add);
            objs.AddRange(add);
        } 
        public void Clear()
        {
            child?.Clear();
            objs.Clear();
        }
        public bool Contains(Object obj) => objs.Contains(obj);
        public void CopyTo(Object[] target, int startInd) => objs.CopyTo(target, startInd);
        public bool Remove(Object obj)
        {
            child?.Remove(obj);
            return objs.Remove(obj);
        }
        public int Count => objs.Count;
        public bool IsReadOnly => false;
        public IEnumerator<Object> GetEnumerator() => objs.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public class ChildList : IList<Object>
        {
            List<Object> objs;
            public ChildList(List<Object> list) => objs = list;
            public int IndexOf(Object obj) => objs.IndexOf(obj);
            public void Insert(int ind, Object obj) => throw new NotSupportedException();
            public void RemoveAt(int ind) => throw new NotSupportedException();
            public Object this[int ind]
            {
                get => objs[ind];
                set => throw new NotSupportedException();
            }
            public void Add(Object obj) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(Object obj) => objs.Contains(obj);
            public void CopyTo(Object[] target, int startInd) => objs.CopyTo(target, startInd);
            public bool Remove(Object obj) => throw new NotSupportedException();
            public int Count => objs.Count;
            public bool IsReadOnly => true;
            public IEnumerator<Object> GetEnumerator() => objs.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public List<Object> EXPOSE() => objs;
        }
    }
}