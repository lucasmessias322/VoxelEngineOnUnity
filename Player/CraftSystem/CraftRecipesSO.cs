using System.Collections.Generic;
using UnityEngine;


[CreateAssetMenu(fileName = "CraftRecipesSO", menuName = "CraftSistem/RecipesSO")]
public class CraftRecipesSO : ScriptableObject
{
public List<Recipe> recipes = new List<Recipe>();
}